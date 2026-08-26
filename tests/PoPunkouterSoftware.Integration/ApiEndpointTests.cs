using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

[Collection("WebApp")]
public class HealthEndpointTests
{
    private readonly HttpClient _client;

    public HealthEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// The whole /health contract in one request. This replaced eight Facts that each
    /// fetched the same document and looked at a different field of it — same coverage,
    /// eight fewer round trips through WebApplicationFactory, and a failure now names the
    /// contract rather than one property of it.
    /// </summary>
    [Fact]
    public async Task GetHealth_Returns200_WithTheFullDeepProbeContract()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.TryGetProperty("status", out _).Should().BeTrue();
        root.TryGetProperty("timestamp", out _).Should().BeTrue();
        root.TryGetProperty("checks", out _).Should().BeTrue();
        root.TryGetProperty("environment", out _).Should().BeTrue();
        root.GetProperty("application").GetString().Should().Be("PoPunkouterSoftware");
        root.GetProperty("config").GetProperty("ASPNETCORE_ENVIRONMENT")
            .GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Static liveness — deliberately separate from the deep probe above.</summary>
    [Fact]
    public async Task GetLiveness_Returns200()
    {
        var response = await _client.GetAsync("/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

[Collection("WebApp")]
public class ConfigEndpointTests
{
    private readonly HttpClient _client;

    public ConfigEndpointTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task GetConfig_Returns200_WithAnAbsoluteApiBase()
    {
        var response = await _client.GetAsync("/api/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("apiBase").GetString()
            .Should().StartWith("http").And.EndWith("/api");
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

        // Folded in from the deleted GetConfig_TestEnvironment_ReportsMockMode: the exact
        // key set above already proves the auth-era fields (guestLoginEnabled,
        // microsoftOAuthEnabled) cannot regrow, so only the value claim was left to keep.
        doc.RootElement.GetProperty("isMockMode").GetBoolean().Should().BeTrue(
            because: "the hermetic fixture runs under the Testing environment");
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

    /// <summary>
    /// The site must not showcase itself. Removing its apps.json entry is not sufficient —
    /// the response merges the catalog with EVERY service the Azure scan discovered, and this
    /// site is a real App Service in the same subscription, so it returns via the inventory
    /// path. Asserted on both the Azure resource name and the friendly name.
    /// </summary>
    [Fact]
    public async Task GetPortfolio_NeverIncludesThisSiteItself()
    {
        var response = await _client.GetAsync("/api/portfolio");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("apps").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString() ?? "")
            .ToList();

        names.Should().NotContain(n => n.Replace("-", "").Equals("PoPunkouterSoftware", StringComparison.OrdinalIgnoreCase));
        names.Should().NotContain(n => n.Contains("app-popunkoutersoftware", StringComparison.OrdinalIgnoreCase));
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
}

[Collection("WebApp")]
public class StaticFilesTests
{
    private readonly HttpClient _client;

    public StaticFilesTests(TestWebApp factory) => _client = factory.CreateClient();
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

