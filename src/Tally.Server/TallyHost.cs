using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace Tally.Server;

public sealed record EventDto(string CustomerId, string EventId, string Meter, decimal Quantity, DateTimeOffset EventTime);

public sealed record CustomerDto(string PlanId, int? PlanVersion, DateOnly StartDate);

public static class TallyHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var apiKey = builder.Configuration["Tally:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 16)
        {
            throw new InvalidOperationException("Tally:ApiKey must be configured with at least 16 characters");
        }

        var connectionString = builder.Configuration["Tally:ConnectionString"]
            ?? throw new InvalidOperationException("Tally:ConnectionString is required");
        var maxFutureHours = builder.Configuration.GetValue("Tally:MaxFutureHours", 24);

        var dataSource = NpgsqlDataSource.Create(connectionString);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(new TallyStore(dataSource));
        builder.Services.AddHostedService<SchemaService>();
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4 * 1024 * 1024);

        var app = builder.Build();
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/health"))
            {
                await next();
                return;
            }

            var header = context.Request.Headers.Authorization.ToString();
            var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(header[7..]) : [];
            if (presented.Length != keyBytes.Length || !CryptographicOperations.FixedTimeEquals(presented, keyBytes))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            try
            {
                await next();
            }
            catch (ArgumentException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapPut("/plans", async (Plan plan, TallyStore store) =>
        {
            var version = await store.PublishPlanAsync(plan);
            return Results.Ok(new { plan.Id, version });
        });

        app.MapPut("/customers/{id}", async (string id, CustomerDto dto, TallyStore store) =>
            await store.CreateCustomerAsync(id, dto.PlanId, dto.PlanVersion, dto.StartDate)
                ? Results.Created($"/customers/{id}", new { id })
                : Results.Ok(new { id, existing = true }));

        app.MapPost("/events", async (EventDto[] events, TallyStore store) =>
        {
            var limit = DateTime.UtcNow.AddHours(maxFutureHours);
            var mapped = events.Select(e => new UsageEvent(e.CustomerId, e.EventId, e.Meter, e.Quantity, e.EventTime.UtcDateTime)).ToList();
            if (mapped.Any(e => e.EventTime > limit))
            {
                throw new ArgumentException($"eventTime must not be more than {maxFutureHours} hours in the future");
            }

            return Results.Ok(await store.IngestAsync(mapped));
        });

        app.MapGet("/customers/{id}/usage", async (string id, string? period, TallyStore store) =>
            await store.UsageAsync(id, period) is { } usage ? Results.Ok(usage) : Results.NotFound());

        app.MapGet("/customers/{id}/invoice-preview", async (string id, string? asOf, TallyStore store) =>
            await store.BillAsync(id, ParseAsOf(asOf), commit: false) is { } outcome ? Results.Ok(outcome) : Results.NotFound());

        app.MapPost("/customers/{id}/invoices", async (string id, string? asOf, TallyStore store) =>
        {
            var outcome = await store.BillAsync(id, ParseAsOf(asOf), commit: true);
            return outcome is null ? Results.NotFound()
                : outcome.Invoice is null ? Results.Ok(outcome)
                : Results.Created($"/customers/{id}/invoices", outcome);
        });

        app.MapGet("/customers/{id}/invoices", async (string id, TallyStore store) => Results.Ok(await store.InvoicesAsync(id)));

        return app;
    }

    private static DateTime ParseAsOf(string? asOf) =>
        asOf is null
            ? DateTime.UtcNow
            : DateTimeOffset.TryParse(asOf, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.UtcDateTime
                : throw new ArgumentException("asOf must be an ISO-8601 timestamp");
}

internal sealed class SchemaService(NpgsqlDataSource dataSource) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => TallyStore.EnsureSchemaAsync(dataSource, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
