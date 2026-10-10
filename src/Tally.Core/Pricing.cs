namespace Tally;

public enum PricingModel
{
    Flat,
    Graduated,
    Volume,
    Package,
}

public sealed record Tier(decimal? UpTo, decimal UnitPrice, decimal FlatFee = 0m);

public sealed record MeterPricing
{
    public required string Meter { get; init; }
    public PricingModel Model { get; init; } = PricingModel.Flat;
    public decimal UnitPrice { get; init; }
    public decimal IncludedFree { get; init; }
    public IReadOnlyList<Tier> Tiers { get; init; } = [];
    public decimal PackageSize { get; init; } = 1m;
    public decimal PackagePrice { get; init; }
}

public sealed record Plan
{
    public required string Id { get; init; }
    public string Currency { get; init; } = "USD";
    public int Decimals { get; init; } = 2;
    public decimal BaseFee { get; init; }
    public IReadOnlyList<MeterPricing> Meters { get; init; } = [];

    public MeterPricing? Find(string meter) => Meters.FirstOrDefault(m => m.Meter == meter);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("id is required");
        }

        if (Decimals is < 0 or > 6)
        {
            errors.Add("decimals must be between 0 and 6");
        }

        if (BaseFee < 0)
        {
            errors.Add("baseFee must not be negative");
        }

        if (Meters.Select(m => m.Meter).Distinct().Count() != Meters.Count)
        {
            errors.Add("meter names must be unique");
        }

        foreach (var meter in Meters)
        {
            var name = $"meter '{meter.Meter}'";
            if (meter.IncludedFree < 0)
            {
                errors.Add($"{name}: includedFree must not be negative");
            }

            switch (meter.Model)
            {
                case PricingModel.Flat:
                    if (meter.UnitPrice < 0)
                    {
                        errors.Add($"{name}: unitPrice must not be negative");
                    }

                    break;
                case PricingModel.Package:
                    if (meter.PackageSize <= 0)
                    {
                        errors.Add($"{name}: packageSize must be positive");
                    }

                    if (meter.PackagePrice < 0)
                    {
                        errors.Add($"{name}: packagePrice must not be negative");
                    }

                    break;
                default:
                    ValidateTiers(name, meter.Tiers, errors);
                    break;
            }
        }

        return errors;
    }

    private static void ValidateTiers(string name, IReadOnlyList<Tier> tiers, List<string> errors)
    {
        if (tiers.Count == 0)
        {
            errors.Add($"{name}: at least one tier is required");
            return;
        }

        decimal previous = 0;
        for (var i = 0; i < tiers.Count; i++)
        {
            var tier = tiers[i];
            if (tier.UnitPrice < 0 || tier.FlatFee < 0)
            {
                errors.Add($"{name}: tier {i + 1} has a negative price");
            }

            if (i == tiers.Count - 1)
            {
                if (tier.UpTo is not null)
                {
                    errors.Add($"{name}: the last tier must be unbounded (upTo null)");
                }
            }
            else if (tier.UpTo is not { } upTo || upTo <= previous)
            {
                errors.Add($"{name}: tier {i + 1} must have an upTo greater than the previous tier");
            }
            else
            {
                previous = upTo;
            }
        }
    }
}

public static class PricingCalculator
{
    public static decimal ExactCost(MeterPricing pricing, decimal quantity)
    {
        var billable = Math.Max(0m, quantity - pricing.IncludedFree);
        if (billable == 0m)
        {
            return 0m;
        }

        switch (pricing.Model)
        {
            case PricingModel.Flat:
                return billable * pricing.UnitPrice;

            case PricingModel.Package:
                return Math.Ceiling(billable / pricing.PackageSize) * pricing.PackagePrice;

            case PricingModel.Volume:
            {
                var tier = pricing.Tiers.FirstOrDefault(t => t.UpTo is null || billable <= t.UpTo) ?? pricing.Tiers[^1];
                return billable * tier.UnitPrice + tier.FlatFee;
            }

            default:
            {
                decimal total = 0;
                decimal lower = 0;
                foreach (var tier in pricing.Tiers)
                {
                    var upper = tier.UpTo ?? decimal.MaxValue;
                    var units = Math.Min(billable, upper) - lower;
                    if (units > 0)
                    {
                        total += units * tier.UnitPrice + tier.FlatFee;
                    }

                    if (billable <= upper)
                    {
                        break;
                    }

                    lower = upper;
                }

                return total;
            }
        }
    }

    public static decimal Cost(Plan plan, MeterPricing pricing, decimal quantity) =>
        Round(plan, ExactCost(pricing, quantity));

    public static decimal Round(Plan plan, decimal amount) =>
        Math.Round(amount, plan.Decimals, MidpointRounding.AwayFromZero);

    public static decimal ProratedBaseFee(Plan plan, DateOnly start, string period)
    {
        var (first, next) = PeriodBounds(period);
        var startDay = DateOnly.FromDateTime(start.ToDateTime(TimeOnly.MinValue));
        if (startDay <= first)
        {
            return Round(plan, plan.BaseFee);
        }

        var days = next.DayNumber - first.DayNumber;
        var remaining = next.DayNumber - startDay.DayNumber;
        return Round(plan, plan.BaseFee * remaining / days);
    }

    public static string PeriodOf(DateTime utc) => $"{utc.Year:D4}-{utc.Month:D2}";

    public static (DateOnly First, DateOnly Next) PeriodBounds(string period)
    {
        var first = new DateOnly(int.Parse(period[..4]), int.Parse(period[5..7]), 1);
        return (first, first.AddMonths(1));
    }
}
