using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.IntegrationTests;

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
    public async Task GetConfig_TestEnvironment_IsNotProduction()
    {
        var json = await _client.GetStringAsync("/api/config");
        var doc = JsonDocument.Parse(json);

        // This site has no auth — config exposes only environment/feature state.
        doc.RootElement.GetProperty("isProduction").GetBoolean().Should().BeFalse();
        doc.RootElement.TryGetProperty("guestLoginEnabled", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("microsoftOAuthEnabled", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetConfig_ReturnsModelCatalog_ForAllThreeCategories()
    {
        var json = await _client.GetStringAsync("/api/config");
        var doc = JsonDocument.Parse(json);
        var modelCatalog = doc.RootElement.GetProperty("modelCatalog");

        modelCatalog.GetProperty("remote").GetArrayLength().Should().BeGreaterThan(0);
        modelCatalog.GetProperty("browser").GetArrayLength().Should().BeGreaterThan(0);
        modelCatalog.GetProperty("ollama").GetArrayLength().Should().BeGreaterThan(0);
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
