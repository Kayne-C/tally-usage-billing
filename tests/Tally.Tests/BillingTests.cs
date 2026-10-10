using Npgsql;
using Xunit;

namespace Tally.Tests;

[Collection("postgres")]
public class BillingTests(PostgresFixture fixture)
{
    private static UsageEvent Event(string customer, string id, string meter, decimal quantity, string time) =>
        new(customer, id, meter, quantity, World.Utc(time));

    private static MeterPricing Api => World.Standard.Find("api.calls")!;

    [Fact]
    public async Task A_closed_month_is_billed_with_the_base_fee_and_tiered_usage()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 2100, "2026-01-12T00:00:00Z"), Event("c1", "e2", "storage.gb", 8, "2026-01-20T00:00:00Z")]);

        var outcome = await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true);

        var invoice = outcome!.Invoice!;
        Assert.Equal(1, invoice.Number);
        Assert.Equal(["base", "usage", "usage"], invoice.Lines.Select(l => l.Kind));
        Assert.Equal(100m, invoice.Lines[0].Amount);
        var expectedApi = PricingCalculator.Cost(World.Standard, Api, 2100);
        Assert.Equal(expectedApi, invoice.Lines.Single(l => l.Meter == "api.calls").Amount);
        Assert.Equal(1m, invoice.Lines.Single(l => l.Meter == "storage.gb").Amount);
        Assert.Equal(100m + expectedApi + 1m, invoice.Total);
    }

    [Fact]
    public async Task Billing_twice_does_not_bill_anything_twice()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 500, "2026-01-12T00:00:00Z")]);

        var first = await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true);
        var second = await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true);

        Assert.NotNull(first!.Invoice);
        Assert.Null(second!.Invoice);
        Assert.Single(await world.Store.InvoicesAsync("c1"));
    }

    [Fact]
    public async Task The_month_in_progress_is_never_billed()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 500, "2026-01-12T00:00:00Z")]);

        var outcome = await world.Store.BillAsync("c1", World.Utc("2026-01-31T23:59:59Z"), commit: true);

        Assert.Null(outcome!.Invoice);
    }

    [Fact]
    public async Task Late_usage_becomes_an_adjustment_equal_to_the_difference_of_cumulative_prices()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 900, "2026-01-12T00:00:00Z")]);
        var first = (await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true))!.Invoice!;

        await world.Store.IngestAsync([Event("c1", "late", "api.calls", 5000, "2026-01-31T23:00:00Z")]);
        var second = (await world.Store.BillAsync("c1", World.Utc("2026-02-10T00:00:00Z"), commit: true))!.Invoice!;

        var line = Assert.Single(second.Lines);
        Assert.Equal("adjustment", line.Kind);
        Assert.Equal(5000m, line.Quantity);
        var before = PricingCalculator.Cost(World.Standard, Api, 900);
        var after = PricingCalculator.Cost(World.Standard, Api, 5900);
        Assert.Equal(after - before, line.Amount);
        Assert.Equal(2, second.Number);
        Assert.Equal(first.Lines.Single(l => l.Meter == "api.calls").Amount + line.Amount, after);
    }

    [Fact]
    public async Task Invoice_numbers_are_gapless_and_totals_equal_their_lines()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        for (var month = 1; month <= 4; month++)
        {
            await world.Store.IngestAsync([Event("c1", $"e{month}", "sms", 130, $"2026-0{month}-10T00:00:00Z")]);
            await world.Store.BillAsync("c1", new DateTime(2026, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1), commit: true);
        }

        var invoices = await world.Store.InvoicesAsync("c1");

        Assert.Equal([1, 2, 3, 4], invoices.Select(i => i.Number));
        Assert.All(invoices, i => Assert.Equal(i.Lines.Sum(l => l.Amount), i.Total));
        Assert.All(invoices, i => Assert.Equal(["base", "usage"], i.Lines.Select(l => l.Kind)));
        Assert.All(invoices, i => Assert.Equal(107m, i.Total));
    }

    [Fact]
    public async Task A_customer_who_starts_mid_month_pays_a_prorated_first_base_fee_and_never_a_duplicate()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1", start: "2026-01-16");

        var january = (await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true))!.Invoice!;
        var catchUp = (await world.Store.BillAsync("c1", World.Utc("2026-04-01T00:00:00Z"), commit: true))!.Invoice!;

        Assert.Equal(51.61m, january.Total);
        Assert.Equal(["2026-02", "2026-03"], catchUp.Lines.Select(l => l.Period));
        Assert.Equal(200m, catchUp.Total);
    }

    [Fact]
    public async Task A_preview_changes_nothing()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 500, "2026-01-12T00:00:00Z")]);

        var preview = await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: false);
        var real = await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true);

        Assert.Equal(0, preview!.Invoice!.Id);
        Assert.Equal(preview.Invoice.Total, real!.Invoice!.Total);
        Assert.Equal(1, real.Invoice.Number);
    }

    [Fact]
    public async Task Meters_missing_from_the_plan_are_reported_and_never_billed()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.IngestAsync([Event("c1", "e1", "mystery", 10, "2026-01-12T00:00:00Z")]);

        var outcome = await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true);

        Assert.Equal(["mystery"], outcome!.UnpricedMeters);
        Assert.Equal(["base"], outcome.Invoice!.Lines.Select(l => l.Kind));
    }

    [Fact]
    public async Task Issued_invoices_cannot_be_updated_or_deleted()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true);

        foreach (var sql in new[]
        {
            "UPDATE invoices SET total = 0",
            "DELETE FROM invoices",
            "UPDATE invoice_lines SET amount = 0",
            "DELETE FROM invoice_lines",
        })
        {
            await using var cmd = world.DataSource.CreateCommand(sql);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("immutable", ex.MessageText);
        }
    }

    [Fact]
    public async Task Customers_stay_on_the_plan_version_they_subscribed_to()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("old");
        var cheaper = World.Standard with { BaseFee = 40m };
        var version = await world.Store.PublishPlanAsync(cheaper);
        await world.Store.CreateCustomerAsync("new", "standard", null, DateOnly.Parse("2026-01-01"));

        var old = (await world.Store.BillAsync("old", World.Utc("2026-02-01T00:00:00Z"), commit: true))!.Invoice!;
        var fresh = (await world.Store.BillAsync("new", World.Utc("2026-02-01T00:00:00Z"), commit: true))!.Invoice!;

        Assert.Equal(2, version);
        Assert.Equal(100m, old.Total);
        Assert.Equal(40m, fresh.Total);
    }

    [Fact]
    public async Task Ten_simultaneous_billing_runs_produce_one_invoice()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 3000, "2026-01-12T00:00:00Z")]);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => world.Store.BillAsync("c1", World.Utc("2026-02-01T00:00:00Z"), commit: true))));

        Assert.Equal(1, outcomes.Count(o => o!.Invoice is not null));
        Assert.Single(await world.Store.InvoicesAsync("c1"));
    }

    [Fact]
    public async Task Unknown_customers_have_no_invoices_or_usage()
    {
        await using var world = await fixture.CreateWorldAsync();

        Assert.Null(await world.Store.BillAsync("ghost", DateTime.UtcNow, commit: true));
        Assert.Null(await world.Store.UsageAsync("ghost", null));
    }
}
