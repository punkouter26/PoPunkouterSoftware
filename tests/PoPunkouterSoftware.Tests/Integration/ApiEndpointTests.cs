using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Tests.Integration;

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
    public async Task GetLiveness_Returns200()
    {
        var response = await _client.GetAsync("/healthz");
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

    [Fact]
    public async Task GetConfig_TestEnvironment_ReportsMockMode()
    {
        var json = await _client.GetStringAsync("/api/config");
        var doc = JsonDocument.Parse(json);

        // This site has no auth — config exposes only environment/feature state.
        doc.RootElement.GetProperty("isMockMode").GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("guestLoginEnabled", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("microsoftOAuthEnabled", out _).Should().BeFalse();
    }

    /// <summary>
    /// The contract is exactly the three fields the WASM client's ConfigResponse binds.
    /// Anything else is payload nobody reads — this pins the response closed so it cannot
    /// silently regrow the discarded fields (isProduction, AI flags, model catalogue).
    /// </summary>
    [Fact]
    public async Task GetConfig_ReturnsExactlyTheThreeFieldsTheClientBinds()
    {
        var json = await _client.GetStringAsync("/api/config");
        var doc = JsonDocument.Parse(json);

        doc.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("apiBase", "isMockMode", "managementActionsEnabled");
    }
}

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

[Collection("WebApp")]
public class OpsSummaryEndpointTests
{
    private readonly HttpClient _client;

    public OpsSummaryEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetSummary_ReturnsCompactStatusContract()
    {
        var response = await _client.GetAsync("/api/diag/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("healthPercent", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("attentionItems", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("responseTimes", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("webServices", out _).Should().BeFalse(
            because: "the first-paint summary must not return the full report graph");
    }
}

[Collection("WebApp")]
public class PortfolioEndpointTests
{
    private readonly HttpClient _client;

    public PortfolioEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetPortfolio_ReturnsStableDecoratedCatalog()
    {
        var response = await _client.GetAsync("/api/portfolio");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        apps.ValueKind.Should().Be(JsonValueKind.Array);
        apps.GetArrayLength().Should().BeGreaterThan(0);
        foreach (var app in apps.EnumerateArray())
        {
            app.TryGetProperty("id", out _).Should().BeTrue();
            app.TryGetProperty("description", out var description).Should().BeTrue();
            description.GetString().Should().NotBeNullOrWhiteSpace();
            app.TryGetProperty("status", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetPortfolio_EnvelopeCarriesFreshness_SoTheUiCanBeHonestAboutLive()
    {
        var response = await _client.GetAsync("/api/portfolio");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("stale", out var stale).Should().BeTrue();
        stale.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        doc.RootElement.TryGetProperty("refreshInProgress", out var refreshing).Should().BeTrue();
        refreshing.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task GetPortfolio_CardUrlsComeFromTheCuratedCatalog_NeverBlank()
    {
        // The catalog URL wins over scanned inventory so a card can never point a
        // visitor at a decommissioned host from a weeks-old report.
        var response = await _client.GetAsync("/api/portfolio");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var app in doc.RootElement.GetProperty("apps").EnumerateArray())
        {
            var url = app.GetProperty("url").GetString();
            url.Should().NotBeNullOrWhiteSpace();
            Uri.TryCreate(url, UriKind.Absolute, out _).Should().BeTrue(
                because: $"'{app.GetProperty("name").GetString()}' must have an absolute URL");
        }
    }
}

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

[Collection("WebApp")]
public class AzureAutomationScriptEndpointTests
{
    private readonly HttpClient _client;

    public AzureAutomationScriptEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetAutomationScript_ReturnsDownloadablePowerShellScript()
    {
        var response = await _client.GetAsync("/api/diag/automation-script");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
        response.Content.Headers.ContentDisposition?.FileNameStar.Should().Be("New-AzureEfficiencyReport.ps1");

        var script = await response.Content.ReadAsStringAsync();
        script.Should().Contain("az login");
        script.Should().Contain("\"group\", \"list\"");
        script.Should().Contain("--skip-token");
        script.Should().Contain("azure-inventory-report.html");
        script.Should().Contain("cleanup_suggestions.ps1");
        script.Should().NotContain("keyvault\", \"secret\", \"show");
    }
}
