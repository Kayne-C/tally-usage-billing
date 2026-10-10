using System.Collections.Concurrent;
using Xunit;

namespace Tally.Tests;

[Collection("postgres")]
public class IngestTests(PostgresFixture fixture)
{
    private static UsageEvent Event(string customer, string id, string meter, decimal quantity, string time) =>
        new(customer, id, meter, quantity, World.Utc(time));

    [Fact]
    public async Task The_same_event_delivered_twice_counts_once()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");

        var first = await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 10, "2026-01-05T10:00:00Z")]);
        var second = await world.Store.IngestAsync([Event("c1", "e1", "api.calls", 10, "2026-01-05T10:00:00Z")]);

        Assert.Equal(new IngestResult(1, 1, 0, 0), first);
        Assert.Equal(new IngestResult(1, 0, 1, 0), second);
        var usage = await world.Store.UsageAsync("c1", "2026-01");
        Assert.Equal(10m, Assert.Single(usage!).Quantity);
    }

    [Fact]
    public async Task Duplicates_inside_one_batch_count_once()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");

        var result = await world.Store.IngestAsync(
        [
            Event("c1", "e1", "api.calls", 5, "2026-01-05T10:00:00Z"),
            Event("c1", "e1", "api.calls", 5, "2026-01-05T10:00:00Z"),
            Event("c1", "e2", "api.calls", 7, "2026-01-05T10:00:00Z"),
        ]);

        Assert.Equal(new IngestResult(3, 2, 1, 0), result);
        Assert.Equal(12m, (await world.Store.UsageAsync("c1", null))!.Single().Quantity);
    }

    [Fact]
    public async Task Events_for_unknown_customers_or_before_the_subscription_started_are_rejected_not_stored()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1", start: "2026-03-01");

        var result = await world.Store.IngestAsync(
        [
            Event("ghost", "e1", "api.calls", 1, "2026-03-05T00:00:00Z"),
            Event("c1", "e2", "api.calls", 1, "2026-02-28T23:59:59Z"),
            Event("c1", "e3", "api.calls", 1, "2026-03-01T00:00:00Z"),
        ]);

        Assert.Equal(new IngestResult(3, 1, 0, 2), result);
        Assert.Equal(1L, await world.ScalarAsync<long>("SELECT count(*) FROM usage_events"));
    }

    [Theory]
    [InlineData("2026-01-31T23:59:59.999Z", "2026-01")]
    [InlineData("2026-02-01T00:00:00Z", "2026-02")]
    [InlineData("2026-12-31T23:59:59Z", "2026-12")]
    public async Task Events_are_assigned_to_the_utc_month_of_their_event_time(string time, string period)
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");

        await world.Store.IngestAsync([Event("c1", "e1", "storage.gb", 1, time)]);

        Assert.Equal(period, Assert.Single((await world.Store.UsageAsync("c1", null))!).Period);
    }

    [Fact]
    public async Task Invalid_events_reject_the_whole_batch_before_touching_the_database()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        var good = Event("c1", "ok", "api.calls", 1, "2026-01-05T00:00:00Z");

        await Assert.ThrowsAsync<ArgumentException>(() => world.Store.IngestAsync([good, Event("c1", "z", "api.calls", 0, "2026-01-05T00:00:00Z")]));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Store.IngestAsync([good, Event("c1", "n", "api.calls", -3, "2026-01-05T00:00:00Z")]));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Store.IngestAsync([good, Event("c1", "m", "Bad Meter!", 1, "2026-01-05T00:00:00Z")]));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Store.IngestAsync([good, Event("c1", "", "api.calls", 1, "2026-01-05T00:00:00Z")]));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Store.IngestAsync([good, new UsageEvent("c1", "t", "api.calls", 1, new DateTime(2026, 1, 5))]));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Store.IngestAsync(Enumerable.Range(0, TallyStore.MaxBatch + 1).Select(i => Event("c1", $"b{i}", "api.calls", 1, "2026-01-05T00:00:00Z")).ToList()));

        Assert.Equal(0L, await world.ScalarAsync<long>("SELECT count(*) FROM usage_events"));
    }

    [Fact]
    public async Task Fifty_concurrent_retries_of_the_same_batch_count_every_event_once()
    {
        await using var world = await fixture.CreateWorldAsync();
        await world.SeedCustomerAsync("c1");
        var batch = Enumerable.Range(0, 200).Select(i => Event("c1", $"e{i}", i % 2 == 0 ? "api.calls" : "storage.gb", 1.5m, "2026-01-10T00:00:00Z")).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => world.Store.IngestAsync(batch))));

        Assert.Equal(200, results.Sum(r => r.Accepted));
        Assert.Equal(200L * 49, results.Sum(r => (long)r.Duplicates));
        Assert.Equal(300m, (await world.Store.UsageAsync("c1", null))!.Sum(u => u.Quantity));
    }

    [Fact]
    public async Task Rollups_equal_the_sum_of_distinct_events_under_concurrent_overlapping_writers()
    {
        await using var world = await fixture.CreateWorldAsync();
        var customers = new[] { "a", "b", "c" };
        foreach (var c in customers)
        {
            await world.SeedCustomerAsync(c);
        }

        var meters = new[] { "api.calls", "storage.gb", "sms" };
        var random = new Random(2024);
        var distinct = new Dictionary<(string, string), UsageEvent>();
        for (var i = 0; i < 6000; i++)
        {
            var customer = customers[random.Next(customers.Length)];
            var month = random.Next(1, 4);
            var e = new UsageEvent(customer, $"ev-{i}", meters[random.Next(meters.Length)], random.Next(1, 100000) / 1000m,
                new DateTime(2026, month, random.Next(1, 28), random.Next(0, 24), random.Next(0, 60), 0, DateTimeKind.Utc));
            distinct[(customer, e.EventId)] = e;
        }

        var deliveries = distinct.Values.SelectMany(e => Enumerable.Repeat(e, random.Next(1, 4))).OrderBy(_ => random.Next()).ToList();
        var chunks = new ConcurrentQueue<List<UsageEvent>>();
        for (var i = 0; i < deliveries.Count;)
        {
            var size = random.Next(1, 120);
            chunks.Enqueue(deliveries.Skip(i).Take(size).ToList());
            i += size;
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (chunks.TryDequeue(out var chunk))
            {
                await world.Store.IngestAsync(chunk);
            }
        })));

        var expected = distinct.Values
            .GroupBy(e => (e.CustomerId, e.Meter, Period: PricingCalculator.PeriodOf(e.EventTime)))
            .ToDictionary(g => g.Key, g => (Quantity: g.Sum(e => e.Quantity), Count: g.LongCount()));
        var actual = new Dictionary<(string, string, string), (decimal, long)>();
        foreach (var c in customers)
        {
            foreach (var u in (await world.Store.UsageAsync(c, null))!)
            {
                actual[(c, u.Meter, u.Period)] = (u.Quantity, u.Events);
            }
        }

        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, value) in expected)
        {
            Assert.Equal(value, actual[key]);
        }

        Assert.Equal(distinct.Count, await world.ScalarAsync<int>("SELECT count(*) FROM usage_events"));
        Assert.Equal(0L, await world.ScalarAsync<long>(
            "SELECT count(*) FROM usage_rollups r WHERE r.quantity <> (SELECT sum(quantity) FROM usage_events e WHERE e.customer_id = r.customer_id AND e.meter = r.meter AND e.period = r.period)"));
    }
}
