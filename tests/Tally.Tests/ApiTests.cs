using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tally.Server;
using Xunit;

namespace Tally.Tests;

[Collection("postgres")]
public class ApiTests(PostgresFixture fixture)
{
    private const string Key = "test-api-key-0000000001";

    private static async Task<(WebApplication App, HttpClient Http)> StartAsync(string connectionString)
    {
        var app = TallyHost.Build([], b =>
        {
            b.Logging.ClearProviders();
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tally:ConnectionString"] = connectionString,
                ["Tally:ApiKey"] = Key,
            });
        });
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        return (app, http);
    }

    private static readonly object PlanBody = new
    {
        id = "pro",
        baseFee = 50,
        meters = new object[]
        {
            new { meter = "api.calls", model = "Graduated", includedFree = 0, tiers = new object[] { new { upTo = 1000, unitPrice = 0.01 }, new { upTo = (int?)null, unitPrice = 0.005 } } },
        },
    };

    [Fact]
    public async Task Requests_without_the_key_are_rejected_and_health_is_open()
    {
        await using var world = await fixture.CreateWorldAsync();
        var (app, http) = await StartAsync(world.ConnectionString);
        using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/customers/x/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task A_customer_goes_from_plan_to_events_to_invoice_over_http()
    {
        await using var world = await fixture.CreateWorldAsync();
        var (app, http) = await StartAsync(world.ConnectionString);

        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync("/plans", PlanBody)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await http.PutAsJsonAsync("/customers/acme", new { planId = "pro", startDate = "2026-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync("/customers/acme", new { planId = "pro", startDate = "2026-01-01" })).StatusCode);

        var events = new[]
        {
            new { customerId = "acme", eventId = "1", meter = "api.calls", quantity = 1500m, eventTime = "2026-01-15T10:00:00Z" },
            new { customerId = "acme", eventId = "1", meter = "api.calls", quantity = 1500m, eventTime = "2026-01-15T10:00:00Z" },
        };
        var ingest = await (await http.PostAsJsonAsync("/events", events)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, ingest.GetProperty("accepted").GetInt32());
        Assert.Equal(1, ingest.GetProperty("duplicates").GetInt32());

        var usage = await http.GetFromJsonAsync<JsonElement>("/customers/acme/usage?period=2026-01");
        Assert.Equal(12.5m, usage[0].GetProperty("cost").GetDecimal());

        var preview = await http.GetFromJsonAsync<JsonElement>("/customers/acme/invoice-preview?asOf=2026-02-01T00:00:00Z");
        Assert.Equal(62.5m, preview.GetProperty("invoice").GetProperty("total").GetDecimal());

        var created = await http.PostAsync("/customers/acme/invoices?asOf=2026-02-01T00:00:00Z", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var listed = await http.GetFromJsonAsync<JsonElement>("/customers/acme/invoices");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal(62.5m, listed[0].GetProperty("total").GetDecimal());

        var again = await http.PostAsync("/customers/acme/invoices?asOf=2026-02-01T00:00:00Z", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task Bad_input_is_a_400_with_a_reason_and_unknown_customers_are_404()
    {
        await using var world = await fixture.CreateWorldAsync();
        var (app, http) = await StartAsync(world.ConnectionString);
        await http.PutAsJsonAsync("/plans", PlanBody);
        await http.PutAsJsonAsync("/customers/acme", new { planId = "pro", startDate = "2026-01-01" });

        var negative = await http.PostAsJsonAsync("/events", new[] { new { customerId = "acme", eventId = "1", meter = "api.calls", quantity = -5m, eventTime = "2026-01-15T10:00:00Z" } });
        var future = await http.PostAsJsonAsync("/events", new[] { new { customerId = "acme", eventId = "2", meter = "api.calls", quantity = 5m, eventTime = DateTime.UtcNow.AddDays(30).ToString("O") } });
        var badPlan = await http.PutAsJsonAsync("/plans", new { id = "bad", baseFee = -1, meters = Array.Empty<object>() });
        var missingPlan = await http.PutAsJsonAsync("/customers/zed", new { planId = "nope", startDate = "2026-01-01" });
        var badDate = await http.PostAsync("/customers/acme/invoices?asOf=yesterday", null);

        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Contains("quantity", await negative.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPlan.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingPlan.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badDate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/customers/ghost/invoices", null)).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void The_server_refuses_to_start_without_an_api_key()
    {
        Assert.Throws<InvalidOperationException>(() => TallyHost.Build([], b =>
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Tally:ConnectionString"] = "Host=x" })));
    }
}
