using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tally.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("public.ecr.aws/docker/library/postgres:17")
        .WithCommand("-c", "max_connections=400")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public async Task<World> CreateWorldAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, MaxPoolSize = 60 }.ConnectionString;
        var dataSource = NpgsqlDataSource.Create(cs);
        await TallyStore.EnsureSchemaAsync(dataSource);
        return new World(dataSource, cs);
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class World(NpgsqlDataSource dataSource, string connectionString) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public string ConnectionString { get; } = connectionString;

    public TallyStore Store { get; } = new(dataSource);

    public static readonly Plan Standard = new()
    {
        Id = "standard",
        BaseFee = 100m,
        Meters =
        [
            new MeterPricing
            {
                Meter = "api.calls",
                Model = PricingModel.Graduated,
                IncludedFree = 100,
                Tiers = [new Tier(1_000, 0.02m), new Tier(10_000, 0.01m), new Tier(null, 0.004m)],
            },
            new MeterPricing { Meter = "storage.gb", Model = PricingModel.Flat, UnitPrice = 0.125m },
            new MeterPricing { Meter = "sms", Model = PricingModel.Package, PackageSize = 100, PackagePrice = 3.5m },
        ],
    };

    public async Task<string> SeedCustomerAsync(string id, Plan? plan = null, string start = "2026-01-01")
    {
        var p = plan ?? Standard;
        await Store.PublishPlanAsync(p);
        await Store.CreateCustomerAsync(id, p.Id, null, DateOnly.Parse(start));
        return id;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    public static DateTime Utc(string iso) => DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
    }
}
