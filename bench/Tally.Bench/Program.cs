using System.Diagnostics;
using Npgsql;
using Tally;
using Testcontainers.PostgreSql;

var events = int.Parse(Arg("events", "500000"));
var customers = int.Parse(Arg("customers", "2000"));
var writers = int.Parse(Arg("writers", "8"));
var batchSize = int.Parse(Arg("batch", "1000"));

await using var container = new PostgreSqlBuilder("public.ecr.aws/docker/library/postgres:17")
    .WithCommand("-c", "max_connections=200")
    .Build();
await container.StartAsync();
var cs = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { MaxPoolSize = 100 }.ConnectionString;
await using var dataSource = NpgsqlDataSource.Create(cs);
await TallyStore.EnsureSchemaAsync(dataSource);
var store = new TallyStore(dataSource);

var plan = new Plan
{
    Id = "bench",
    BaseFee = 49m,
    Meters =
    [
        new MeterPricing { Meter = "api.calls", Model = PricingModel.Graduated, Tiers = [new Tier(10_000, 0.002m), new Tier(null, 0.001m)] },
        new MeterPricing { Meter = "storage.gb", Model = PricingModel.Flat, UnitPrice = 0.02m },
        new MeterPricing { Meter = "sms", Model = PricingModel.Package, PackageSize = 100, PackagePrice = 1.5m },
    ],
};
await store.PublishPlanAsync(plan);
for (var i = 0; i < customers; i++)
{
    await store.CreateCustomerAsync($"c{i}", "bench", null, new DateOnly(2026, 1, 1));
}

var meters = new[] { "api.calls", "storage.gb", "sms" };
var random = new Random(1);
var all = new UsageEvent[events];
for (var i = 0; i < events; i++)
{
    all[i] = new UsageEvent($"c{random.Next(customers)}", $"e{i}", meters[random.Next(3)], random.Next(1, 500),
        new DateTime(2026, random.Next(1, 4), random.Next(1, 28), random.Next(0, 24), 0, 0, DateTimeKind.Utc));
}

var batches = new Queue<UsageEvent[]>();
for (var i = 0; i < all.Length; i += batchSize)
{
    batches.Enqueue(all[i..Math.Min(all.Length, i + batchSize)]);
}

var latencies = new List<double>();
var watch = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
{
    while (true)
    {
        UsageEvent[]? batch;
        lock (batches)
        {
            batch = batches.Count > 0 ? batches.Dequeue() : null;
        }

        if (batch is null)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        await store.IngestAsync(batch);
        lock (latencies)
        {
            latencies.Add(sw.Elapsed.TotalMilliseconds);
        }
    }
})));
var ingest = watch.Elapsed;
latencies.Sort();
Console.WriteLine($"ingest: {events:N0} events, {customers} customers, {writers} writers, batch {batchSize}");
Console.WriteLine($"  {events / ingest.TotalSeconds:N0} events/s, batch latency p50={latencies[latencies.Count / 2]:F0} ms p99={latencies[(int)(latencies.Count * 0.99)]:F0} ms");

watch.Restart();
var duplicates = 0;
await Parallel.ForEachAsync(all.Chunk(batchSize).Take(100), new ParallelOptions { MaxDegreeOfParallelism = writers }, async (chunk, ct) =>
{
    var result = await store.IngestAsync(chunk, ct);
    Interlocked.Add(ref duplicates, result.Duplicates);
});
Console.WriteLine($"  redelivery of {Math.Min(events, 100 * batchSize):N0} events: {duplicates:N0} recognized as duplicates in {watch.Elapsed.TotalSeconds:F1}s");

watch.Restart();
var invoices = 0;
await Parallel.ForEachAsync(Enumerable.Range(0, customers), new ParallelOptions { MaxDegreeOfParallelism = writers }, async (i, ct) =>
{
    var outcome = await store.BillAsync($"c{i}", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), commit: true, ct);
    if (outcome?.Invoice is not null)
    {
        Interlocked.Increment(ref invoices);
    }
});
var billing = watch.Elapsed;
Console.WriteLine($"billing: {invoices} invoices (3 months each) in {billing.TotalSeconds:F1}s = {invoices / billing.TotalSeconds:N0} invoices/s");

await using var check = dataSource.CreateCommand("SELECT count(*), sum(total), (SELECT count(*) FROM invoice_lines) FROM invoices");
await using var reader = await check.ExecuteReaderAsync();
await reader.ReadAsync();
Console.WriteLine($"  {reader.GetInt64(0)} invoices, {reader.GetInt64(2)} lines, total {reader.GetDecimal(1):N2}");

static string Arg(string name, string fallback)
{
    var args = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(args, "--" + name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}
