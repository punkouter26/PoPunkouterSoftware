using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Tests.E2E;

/// <summary>
/// Pure HTTP smoke tests against a LIVE host — plain HttpClient, no WebApplicationFactory,
/// no browser. Target comes from BASE_URL (defaults to a local run on :8000). Every assertion
/// must hold against BOTH a fresh localhost instance and production, so no test asserts an
/// environment name and data-dependent endpoints allow their documented degraded codes.
/// </summary>
public sealed class ApiSmokeFixture : IDisposable
{
    public HttpClient Client { get; } = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8000"),
        // Production cold starts on a small App Service plan can take tens of seconds.
        Timeout = TimeSpan.FromSeconds(60),
    };

    public void Dispose() => Client.Dispose();
}

public class ApiSmokeTests : IClassFixture<ApiSmokeFixture>
{
    private readonly HttpClient _client;

    public ApiSmokeTests(ApiSmokeFixture fixture) => _client = fixture.Client;

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

    // ─── Broad availability sweep ─────────────────────────────────────────────

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/health")]
    [InlineData("/api/config")]
    [InlineData("/api/portfolio")]
    [InlineData("/api/pinger/status")]
    [InlineData("/robots.txt")]
    [InlineData("/openapi/v1.json")]
    public async Task PublicGetEndpoint_Returns200(string path)
    {
        var resp = await _client.GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, because: $"{path} must be available on any live host");
    }

    // ─── Health ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Healthz_Returns200_WithOkStatus()
    {
        var resp = await _client.GetAsync("/healthz");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Health_Returns200_WithApplicationIdentityChecksAndConfig()
    {
        var resp = await _client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("application").GetString().Should().Be("PoPunkouterSoftware");
        doc.RootElement.TryGetProperty("status", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("checks", out var checks).Should().BeTrue();
        checks.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.TryGetProperty("config", out var config).Should().BeTrue();
        config.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task Health_ConfigValues_AreMaskedNotRawSecrets()
    {
        var resp = await _client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        var config = doc.RootElement.GetProperty("config");

        // The environment name is the one deliberately unmasked key; assert it exists
        // (but never assert its VALUE — this must pass on localhost and production alike).
        config.TryGetProperty("ASPNETCORE_ENVIRONMENT", out var envName).Should().BeTrue();
        envName.GetString().Should().NotBeNullOrWhiteSpace();

        foreach (var prop in config.EnumerateObject().Where(p => p.Name != "ASPNETCORE_ENVIRONMENT"))
        {
            var value = prop.Value.GetString();
            var isMasked = value is "(not set)" or "****" || (value?.Contains('*') ?? false);
            isMasked.Should().BeTrue(because: $"config key '{prop.Name}' must be masked, got '{value}'");
        }
    }

    [Fact]
    public async Task ApiHealth_RedirectsToHealth_FinalResponseIs200()
    {
        // HttpClient follows the 302 by default, so the observed response is /health itself.
        var resp = await _client.GetAsync("/api/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("application").GetString().Should().Be("PoPunkouterSoftware",
            because: "the redirect must land on the canonical /health payload");
    }

    // ─── Config ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Config_Returns200_WithApiBaseEndingInApi()
    {
        var resp = await _client.GetAsync("/api/config");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("apiBase").GetString().Should().EndWith("/api");
    }

    [Fact]
    public async Task Config_HasNoAuthFlags_SiteIsNoAuthByDesign()
    {
        var resp = await _client.GetAsync("/api/config");

        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("guestLoginEnabled", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("microsoftOAuthEnabled", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("browser")]
    [InlineData("ollama")]
    public async Task Config_ModelCatalog_ArrayIsNonEmpty(string catalogKey)
    {
        var resp = await _client.GetAsync("/api/config");

        using var doc = await ReadJsonAsync(resp);
        var catalog = doc.RootElement.GetProperty("modelCatalog").GetProperty(catalogKey);
        catalog.ValueKind.Should().Be(JsonValueKind.Array);
        catalog.GetArrayLength().Should().BeGreaterThan(0);
    }

    // ─── Portfolio ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Portfolio_ReturnsNonEmptyEnvelope_EveryItemHasIdStatusDescription()
    {
        var resp = await _client.GetAsync("/api/portfolio");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("stale", out _).Should().BeTrue(
            because: "the UI needs inventory freshness to render honest Live badges");
        var apps = doc.RootElement.GetProperty("apps");
        apps.ValueKind.Should().Be(JsonValueKind.Array);
        apps.GetArrayLength().Should().BeGreaterThan(0,
            because: "the catalog stays visible even with no Azure report");

        foreach (var item in apps.EnumerateArray())
        {
            item.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
            item.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();
            item.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// The original "ghost fleet" bug: cards pointing at decommissioned hosts. Every
    /// rendered card URL must actually resolve and answer. Network-bound by design —
    /// this is the E2E tier, which already requires a running app and real DNS.
    /// </summary>
    [Fact]
    public async Task Portfolio_EveryCardUrl_ActuallyResolvesAndResponds()
    {
        var resp = await _client.GetAsync("/api/portfolio");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);

        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var failures = new List<string>();
        foreach (var item in doc.RootElement.GetProperty("apps").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var url = item.GetProperty("url").GetString();
            try
            {
                // Cold F1 apps 200 slowly; any HTTP answer proves the host exists.
                using var r = await probe.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if ((int)r.StatusCode >= 400)
                    failures.Add($"{name}: {url} -> {(int)r.StatusCode}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {url} -> {ex.GetBaseException().Message}");
            }
        }

        failures.Should().BeEmpty(because: "the portfolio must not showcase apps that no longer exist");
    }

    // ─── Diag ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DiagSummary_Returns200Or404_ShapeIsOpsSummaryNotRawReport()
    {
        var resp = await _client.GetAsync("/api/diag/summary");

        // 404 is the documented "no report yet" state on a fresh host.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        if (resp.StatusCode != HttpStatusCode.OK)
            return;

        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("healthPercent", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("attentionItems", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("responseTimes", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("webServices", out _).Should().BeFalse(
            because: "the summary is a projection, never the raw report graph");
    }

    [Fact]
    public async Task DiagReport_Returns200Or404Or503_SuccessIsJson()
    {
        var resp = await _client.GetAsync("/api/diag/report");

        // 404 = no report yet; 503 = table storage unavailable with no file cache.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
        if (resp.StatusCode == HttpStatusCode.OK)
            resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Diag_WithJsonAccept_ReturnsOkStatus_WithMaskedKeyVaultUri()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/diag");
        request.Headers.Accept.ParseAdd("application/json");

        var resp = await _client.SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("status").GetString().Should().Be("ok");
        doc.RootElement.GetProperty("config").GetProperty("AzureKeyVaultUri").GetString()
            .Should().Contain("*", because: "the vault URI must be rendered masked");
    }

    // ─── Host plumbing ────────────────────────────────────────────────────────

    [Fact]
    public async Task RobotsTxt_Returns200_TextPlain_WithUserAgentDirective()
    {
        var resp = await _client.GetAsync("/robots.txt");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("User-agent");
    }

    [Fact]
    public async Task Favicon_Returns200_WithImageContentType()
    {
        // /favicon.ico 302-redirects to the static asset; HttpClient follows it.
        var resp = await _client.GetAsync("/favicon.ico");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().StartWith("image/");
    }

    // ─── Errors and API docs ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/api/nope/deeper/route")]
    public async Task UnknownApiRoute_Returns404_WithJsonStatusAndPath(string path)
    {
        var resp = await _client.GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        doc.RootElement.GetProperty("path").GetString().Should().Be(path);
    }

    [Fact]
    public async Task OpenApiDocument_Returns200_AndParsesAsJson()
    {
        var resp = await _client.GetAsync("/openapi/v1.json");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ReadJsonAsync(resp);
        doc.RootElement.TryGetProperty("openapi", out _).Should().BeTrue();
    }
}
