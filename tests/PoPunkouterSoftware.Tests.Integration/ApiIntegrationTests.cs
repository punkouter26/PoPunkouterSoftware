using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Features.Azure;
using Testcontainers.Azurite;
using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Tests.Integration;

// ─── Shared factory (created once per process via CollectionFixture) ──────────

public class TestWebApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Prevent bootstrap ReloadableLogger "already frozen" error in test host
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
            });
        });
    }
}

[CollectionDefinition("WebApp")]
public class WebAppCollection : ICollectionFixture<TestWebApp> { }

// ─── Health endpoint ──────────────────────────────────────────────────────────

[Collection("WebApp")]
public class HealthEndpointTests
{
    private readonly HttpClient _client;
    public HealthEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetHealth_Returns200()
    {
        var response = await _client.GetAsync("/api/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHealthAlias_Returns200()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetHealth_ContentType_IsJson()
    {
        var response = await _client.GetAsync("/api/health");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task GetHealth_ReturnsStatusField()
    {
        var json = await _client.GetStringAsync("/api/health");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("status", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_ReturnsApplicationField()
    {
        var json = await _client.GetStringAsync("/api/health");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("application").GetString().Should().Be("PoPunkouterSoftware");
    }

    [Fact]
    public async Task GetHealth_ReturnsTimestamp()
    {
        var json = await _client.GetStringAsync("/api/health");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("timestamp", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_ReturnsChecksObject()
    {
        var json = await _client.GetStringAsync("/api/health");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("checks", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_ReturnsEnvironmentField()
    {
        var json = await _client.GetStringAsync("/api/health");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("environment", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_ConfigMasked_NotEmpty()
    {
        var json = await _client.GetStringAsync("/api/health");
        var doc = JsonDocument.Parse(json);
        var cfg = doc.RootElement.GetProperty("config");
        cfg.GetProperty("ASPNETCORE_ENVIRONMENT").GetString().Should().NotBeNullOrWhiteSpace();
    }
}

// ─── Config endpoint ──────────────────────────────────────────────────────────

[Collection("WebApp")]
public class ConfigEndpointTests
{
    private readonly HttpClient _client;
    public ConfigEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetConfig_Returns200()
    {
        var response = await _client.GetAsync("/api/config");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetConfig_ReturnsApiBase_StartingWithHttp()
    {
        var json = await _client.GetStringAsync("/api/config");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apiBase").GetString().Should().StartWith("http");
    }

    [Fact]
    public async Task GetConfig_ApiBase_EndsWithSlashApi()
    {
        var json = await _client.GetStringAsync("/api/config");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apiBase").GetString().Should().EndWith("/api");
    }
}

// ─── DiagReport endpoint ──────────────────────────────────────────────────────

[Collection("WebApp")]
public class DiagReportEndpointTests
{
    private readonly HttpClient _client;
    public DiagReportEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetDiagReport_Returns200Or404()
    {
        var response = await _client.GetAsync("/api/diag/report");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDiagReport_WhenOk_ContentIsJson()
    {
        var response = await _client.GetAsync("/api/diag/report");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        }
    }
}

// ─── Az-status endpoint ───────────────────────────────────────────────────────

[Collection("WebApp")]
public class AzStatusEndpointTests
{
    private readonly HttpClient _client;
    public AzStatusEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetAzStatus_Returns200()
    {
        var response = await _client.GetAsync("/api/diag/az-status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAzStatus_ReturnsLoggedInField()
    {
        var json = await _client.GetStringAsync("/api/diag/az-status");
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("loggedIn", out _).Should().BeTrue();
    }
}

// ─── OpenAPI endpoint ─────────────────────────────────────────────────────────

[Collection("WebApp")]
public class OpenApiEndpointTests
{
    private readonly HttpClient _client;
    public OpenApiEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetOpenApi_Returns200()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// ─── Static files ─────────────────────────────────────────────────────────────

[Collection("WebApp")]
public class StaticFilesTests
{
    private readonly HttpClient _client;
    public StaticFilesTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetAppsJson_Returns200()
    {
        var response = await _client.GetAsync("/data/apps.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// ─── AzureReportStore — no-connection test (no Docker needed) ────────────────

public class AzureReportStoreNoConnectionTests
{
    [Fact]
    public async Task LoadAsync_WhenConnectionStringIsEmpty_ReturnsFailureResult()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = "",
                ["AzureTableStorage:Endpoint"] = "",
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        var store = new AzureReportStore(logger, config);
        var result = await store.LoadAsync();
        result.Should().NotBeNull();
        result!.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Table client not available");
    }
}

// ─── Azurite Integration — AzureReportStore over real Table Storage ───────────

public class AzureReportStoreAzuriteTests : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private AzureReportStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = _container.GetConnectionString()
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        return new AzureReportStore(logger, config);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_Report()
    {
        var store = BuildStore();
        var original = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "Test-Sub" },
            WebServices = new WebServicesInfo
            {
                Total = 2,
                Services = new List<WebService>
                {
                    new() { Name = "svc-a", Url = "https://svc-a.azurewebsites.net", HttpStatus = "200" },
                    new() { Name = "svc-b", Url = "https://svc-b.azurewebsites.net", HttpStatus = "404" }
                }
            }
        };
        await store.SaveAsync(original);
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        var loaded = result.Value;
        loaded.Should().NotBeNull();
        loaded!.Subscription!.Name.Should().Be("Test-Sub");
        loaded.WebServices!.Services.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveTwice_OnlyLatestDataRemains()
    {
        var store = BuildStore();
        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "First" } });
        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "Second" } });
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value!.Subscription!.Name.Should().Be("Second");
    }

    [Fact]
    public async Task Load_EmptyTable_ReturnsNull()
    {
        var store = BuildStore();
        var result = await store.LoadAsync();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }
}

// ─── API JSON-schema contract tests ──────────────────────────────────────────
// Guard against serialiser casing regressions (e.g. PascalCase vs camelCase).
// These run against the in-memory TestWebApp — no Docker needed.

[Collection("WebApp")]
public class ApiSchemaContractTests
{
    private readonly HttpClient _client;
    public ApiSchemaContractTests(TestWebApp factory) => _client = factory.CreateClient();

    // /api/health ─────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("status")]
    [InlineData("application")]
    [InlineData("timestamp")]
    [InlineData("checks")]
    [InlineData("environment")]
    [InlineData("config")]
    public async Task Health_HasCamelCaseProperty(string property)
    {
        var json = await _client.GetStringAsync("/api/health");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty(property, out _)
            .Should().BeTrue(because: $"/api/health must expose camelCase '{property}'");
    }

    // /api/config ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Config_HasCamelCase_ApiBase()
    {
        var json = await _client.GetStringAsync("/api/config");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("apiBase", out _)
            .Should().BeTrue(because: "/api/config must expose camelCase 'apiBase'");
    }

    // /api/diag/az-status ─────────────────────────────────────────────────────
    [Fact]
    public async Task AzStatus_HasCamelCase_LoggedIn()
    {
        var json = await _client.GetStringAsync("/api/diag/az-status");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("loggedIn", out _)
            .Should().BeTrue(because: "/api/diag/az-status must expose camelCase 'loggedIn'");
    }

    // /api/diag/report ────────────────────────────────────────────────────────
    [Theory]
    [InlineData("generatedAt")]
    [InlineData("webServices")]
    [InlineData("subscription")]
    public async Task DiagReport_WhenOk_HasCamelCaseProperty(string property)
    {
        var response = await _client.GetAsync("/api/diag/report");
        if (response.StatusCode != System.Net.HttpStatusCode.OK) return; // no report seeded — skip

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty(property, out _)
            .Should().BeTrue(because: $"/api/diag/report must expose camelCase '{property}'");
    }

    [Fact]
    public async Task DiagReport_WhenOk_WebServices_TotalIsCamelCase()
    {
        var response = await _client.GetAsync("/api/diag/report");
        if (response.StatusCode != System.Net.HttpStatusCode.OK) return;

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("webServices", out var ws)) return;
        ws.TryGetProperty("total", out _)
            .Should().BeTrue(because: "webServices.total must be camelCase");
    }
}

// ─── Azurite — delta / history tests ─────────────────────────────────────────

public class AzureReportStoreHistoryTests : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private AzureReportStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureTableStorage:ConnectionString"] = _container.GetConnectionString()
            })
            .Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReportStore>.Instance;
        return new AzureReportStore(logger, config);
    }

    [Fact]
    public async Task SaveThenSave_HistoryContainsPreviousReport()
    {
        var store = BuildStore();

        var first = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "History-Sub-1" }
        };
        await store.SaveAsync(first);

        var second = new AzureReport
        {
            GeneratedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Subscription = new SubscriptionInfo { Name = "History-Sub-2" }
        };
        await store.SaveAsync(second);

        // Latest load should reflect the second save
        var latest = await store.LoadAsync();
        latest.IsSuccess.Should().BeTrue();
        latest.Value!.Subscription!.Name.Should().Be("History-Sub-2");

        // History should contain the first report
        var history = await store.LoadHistoryAsync();
        history.IsSuccess.Should().BeTrue();
        history.Value.Should().NotBeNull();
        history.Value!.Should().Contain(r => r.Subscription!.Name == "History-Sub-1",
            because: "the previous report must be persisted to history on each save");
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotThrow_LastWriterWins()
    {
        var store = BuildStore();

        // Seed a first save so the table exists before concurrent writes
        await store.SaveAsync(new AzureReport { Subscription = new SubscriptionInfo { Name = "seed" } });

        var tasks = Enumerable.Range(1, 5).Select(i => store.SaveAsync(new AzureReport
        {
            GeneratedAt = DateTime.UtcNow,
            Subscription = new SubscriptionInfo { Name = $"concurrent-{i}" }
        }));

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
            r.IsSuccess.Should().BeTrue(because: "concurrent saves must not throw"));

        // Final state must be readable — exactly one record survives
        var final = await store.LoadAsync();
        final.IsSuccess.Should().BeTrue();
        final.Value.Should().NotBeNull();
        final.Value!.Subscription!.Name.Should().StartWith("concurrent-");
    }
}
