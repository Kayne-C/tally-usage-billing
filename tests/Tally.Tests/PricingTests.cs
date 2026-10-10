using Xunit;

namespace Tally.Tests;

public class PricingTests
{
    private static readonly Plan Usd = new() { Id = "p" };

    private static MeterPricing Graduated(params Tier[] tiers) => new() { Meter = "m", Model = PricingModel.Graduated, Tiers = tiers };

    [Fact]
    public void Flat_pricing_multiplies_and_rounds_half_away_from_zero()
    {
        var meter = new MeterPricing { Meter = "m", UnitPrice = 0.0025m };

        Assert.Equal(2.5m, PricingCalculator.Cost(Usd, meter, 1000));
        Assert.Equal(0.01m, PricingCalculator.Cost(Usd, meter, 2));
        Assert.Equal(0.00m, PricingCalculator.Cost(Usd, meter, 1));
        Assert.Equal(0.01m, PricingCalculator.Cost(Usd, meter, 3));
    }

    [Fact]
    public void Included_free_units_come_off_first()
    {
        var meter = new MeterPricing { Meter = "m", UnitPrice = 1m, IncludedFree = 100 };

        Assert.Equal(0m, PricingCalculator.Cost(Usd, meter, 100));
        Assert.Equal(1m, PricingCalculator.Cost(Usd, meter, 101));
        Assert.Equal(0m, PricingCalculator.Cost(Usd, meter, 40));
    }

    [Theory]
    [InlineData(50, 5.00)]
    [InlineData(100, 10.00)]
    [InlineData(101, 10.05)]
    [InlineData(500, 30.00)]
    [InlineData(1000, 55.00)]
    [InlineData(1001, 55.01)]
    [InlineData(2000, 65.00)]
    public void Graduated_pricing_charges_each_unit_at_its_own_tier(decimal quantity, decimal expected)
    {
        var meter = Graduated(new Tier(100, 0.10m), new Tier(1000, 0.05m), new Tier(null, 0.01m));

        Assert.Equal(expected, PricingCalculator.Cost(Usd, meter, quantity));
    }

    [Fact]
    public void Graduated_tiers_can_carry_a_flat_fee_charged_when_the_tier_is_entered()
    {
        var meter = Graduated(new Tier(100, 0.10m), new Tier(null, 0.05m, FlatFee: 2m));

        Assert.Equal(10m, PricingCalculator.Cost(Usd, meter, 100));
        Assert.Equal(12.05m, PricingCalculator.Cost(Usd, meter, 101));
    }

    [Fact]
    public void Volume_pricing_charges_all_units_at_the_tier_the_total_falls_in()
    {
        var meter = new MeterPricing { Meter = "m", Model = PricingModel.Volume, Tiers = [new Tier(100, 0.10m), new Tier(null, 0.05m)] };

        Assert.Equal(10m, PricingCalculator.Cost(Usd, meter, 100));
        Assert.Equal(5.05m, PricingCalculator.Cost(Usd, meter, 101));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(1000, 5)]
    [InlineData(1001, 10)]
    [InlineData(2500, 15)]
    public void Package_pricing_rounds_up_to_whole_packages(decimal quantity, decimal expected)
    {
        var meter = new MeterPricing { Meter = "m", Model = PricingModel.Package, PackageSize = 1000, PackagePrice = 5m };

        Assert.Equal(expected, PricingCalculator.Cost(Usd, meter, quantity));
    }

    [Fact]
    public void Currencies_without_minor_units_round_to_whole_amounts()
    {
        var yen = Usd with { Currency = "JPY", Decimals = 0 };
        var meter = new MeterPricing { Meter = "m", UnitPrice = 0.5m };

        Assert.Equal(2m, PricingCalculator.Cost(yen, meter, 3));
    }

    [Theory]
    [InlineData("2026-10-01", 100.00)]
    [InlineData("2026-10-16", 51.61)]
    [InlineData("2026-10-31", 3.23)]
    [InlineData("2026-09-10", 100.00)]
    public void The_base_fee_is_prorated_by_remaining_days_in_the_first_month(string start, decimal expected)
    {
        var plan = Usd with { BaseFee = 100m };

        Assert.Equal(expected, PricingCalculator.ProratedBaseFee(plan, DateOnly.Parse(start), "2026-10"));
    }

    [Fact]
    public void Invalid_plans_report_every_problem()
    {
        var plan = new Plan
        {
            Id = "bad",
            BaseFee = -1,
            Meters =
            [
                new MeterPricing { Meter = "a", Model = PricingModel.Graduated, Tiers = [new Tier(100, 1), new Tier(50, 1), new Tier(10, 1)] },
                new MeterPricing { Meter = "a", Model = PricingModel.Package, PackageSize = 0 },
                new MeterPricing { Meter = "c", Model = PricingModel.Volume },
            ],
        };

        var errors = plan.Validate();

        Assert.Contains(errors, e => e.Contains("baseFee"));
        Assert.Contains(errors, e => e.Contains("unique"));
        Assert.Contains(errors, e => e.Contains("tier 2"));
        Assert.Contains(errors, e => e.Contains("unbounded"));
        Assert.Contains(errors, e => e.Contains("packageSize"));
        Assert.Contains(errors, e => e.Contains("at least one tier"));
    }

    [Fact]
    public void Graduated_cost_matches_a_unit_by_unit_oracle_on_random_plans()
    {
        var random = new Random(1234);
        for (var round = 0; round < 300; round++)
        {
            var tierCount = random.Next(1, 5);
            var bounds = Enumerable.Range(0, tierCount - 1).Select(_ => random.Next(1, 200)).Distinct().Order().ToList();
            var tiers = bounds.Select(b => new Tier(b, random.Next(1, 100) / 100m, random.Next(0, 3) == 0 ? random.Next(1, 500) / 100m : 0))
                .Append(new Tier(null, random.Next(1, 100) / 100m, random.Next(0, 3) == 0 ? random.Next(1, 500) / 100m : 0))
                .ToArray();
            var free = random.Next(0, 3) == 0 ? random.Next(1, 50) : 0;
            var meter = Graduated(tiers) with { IncludedFree = free };

            for (var quantity = 0; quantity <= 400; quantity += random.Next(1, 40))
            {
                Assert.Equal(Oracle(tiers, free, quantity), PricingCalculator.ExactCost(meter, quantity));
            }
        }
    }

    [Fact]
    public void Graduated_cost_never_decreases_as_usage_grows()
    {
        var random = new Random(99);
        for (var round = 0; round < 200; round++)
        {
            var tiers = new[] { new Tier(random.Next(1, 100), random.Next(1, 50) / 100m), new Tier(random.Next(101, 500), random.Next(1, 50) / 100m), new Tier(null, random.Next(1, 50) / 100m) };
            var meter = Graduated(tiers);
            decimal previous = 0;
            for (decimal quantity = 0; quantity <= 700; quantity += 0.75m)
            {
                var cost = PricingCalculator.Cost(Usd, meter, quantity);
                Assert.True(cost >= previous, $"cost fell from {previous} to {cost} at {quantity}");
                previous = cost;
            }
        }
    }

    private static decimal Oracle(Tier[] tiers, int free, int quantity)
    {
        decimal total = 0;
        var billable = Math.Max(0, quantity - free);
        var entered = new HashSet<int>();
        for (var unit = 1; unit <= billable; unit++)
        {
            var index = 0;
            while (tiers[index].UpTo is { } upTo && unit > upTo)
            {
                index++;
            }

            total += tiers[index].UnitPrice;
            if (entered.Add(index))
            {
                total += tiers[index].FlatFee;
            }
        }

        return total;
    }
}
