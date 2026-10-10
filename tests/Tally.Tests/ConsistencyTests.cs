using System.Collections.Concurrent;
using Xunit;

namespace Tally.Tests;

[Collection("postgres")]
public class ConsistencyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Whatever_the_batching_ordering_duplication_and_billing_interleaving_the_billed_total_equals_the_price_of_the_final_usage()
    {
        await using var world = await fixture.CreateWorldAsync();
        var customers = Enumerable.Range(0, 6).Select(i => $"cust-{i}").ToArray();
        foreach (var c in customers)
        {
            await world.SeedCustomerAsync(c);
        }

        var meters = new[] { "api.calls", "storage.gb", "sms" };
        var random = new Random(777);
        var distinct = new Dictionary<(string, string), UsageEvent>();
        for (var i = 0; i < 9000; i++)
        {
            var customer = customers[random.Next(customers.Length)];
            var e = new UsageEvent(customer, $"ev-{i}", meters[random.Next(meters.Length)], random.Next(1, 600000) / 1000m,
                new DateTime(2026, random.Next(1, 4), random.Next(1, 28), random.Next(0, 24), random.Next(0, 60), random.Next(0, 60), DateTimeKind.Utc));
            distinct[(customer, e.EventId)] = e;
        }

        var deliveries = distinct.Values.SelectMany(e => Enumerable.Repeat(e, random.Next(1, 3))).OrderBy(_ => random.Next()).ToList();
        var chunks = new ConcurrentQueue<List<UsageEvent>>();
        for (var i = 0; i < deliveries.Count;)
        {
            var size = random.Next(1, 150);
            chunks.Enqueue(deliveries.Skip(i).Take(size).ToList());
            i += size;
        }

        var asOf = World.Utc("2026-04-01T00:00:00Z");
        var writing = true;
        var biller = Task.Run(async () =>
        {
            var rng = new Random(5);
            while (Volatile.Read(ref writing))
            {
                foreach (var c in customers.OrderBy(_ => rng.Next()))
                {
                    await world.Store.BillAsync(c, asOf, commit: true);
                }

                await Task.Delay(5);
            }
        });
        var billers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            while (Volatile.Read(ref writing))
            {
                await world.Store.BillAsync(customers[Random.Shared.Next(customers.Length)], asOf, commit: true);
                await Task.Delay(3);
            }
        })).Append(biller).ToList();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (chunks.TryDequeue(out var chunk))
            {
                await world.Store.IngestAsync(chunk);
            }
        })));
        Volatile.Write(ref writing, false);
        await Task.WhenAll(billers);
        foreach (var c in customers)
        {
            await world.Store.BillAsync(c, asOf, commit: true);
        }

        var plan = World.Standard;
        var expected = distinct.Values
            .GroupBy(e => (e.CustomerId, e.Meter, Period: PricingCalculator.PeriodOf(e.EventTime)))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity));

        foreach (var c in customers)
        {
            var invoices = await world.Store.InvoicesAsync(c);
            Assert.Equal(Enumerable.Range(1, invoices.Count), invoices.Select(i => i.Number));
            Assert.All(invoices, i => Assert.Equal(i.Lines.Sum(l => l.Amount), i.Total));

            var lines = invoices.SelectMany(i => i.Lines).ToList();
            Assert.Equal(3, lines.Count(l => l.Kind == "base"));
            Assert.Equal(100m * 3, lines.Where(l => l.Kind == "base").Sum(l => l.Amount));

            foreach (var ((customer, meter, period), quantity) in expected.Where(kv => kv.Key.Item1 == c))
            {
                var billed = lines.Where(l => l.Kind != "base" && l.Meter == meter && l.Period == period).ToList();
                Assert.Equal(quantity, billed.Sum(l => l.Quantity));
                Assert.Equal(PricingCalculator.Cost(plan, plan.Find(meter)!, quantity), billed.Sum(l => l.Amount));
                Assert.Equal(1, billed.Count(l => l.Kind == "usage"));
            }
        }

        Assert.Equal(0L, await world.ScalarAsync<long>("SELECT count(*) FROM usage_rollups WHERE quantity <> billed_quantity"));
    }
}
