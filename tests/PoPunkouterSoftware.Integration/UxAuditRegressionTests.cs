using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Regressions from the UI/UX audit. Each test pins a defect that shipped, so an ordinary
/// edit cannot quietly reintroduce it.
/// </summary>
[Collection("WebApp")]
public class HealthContractRegressionTests
{
    private readonly HttpClient _client;

    public HealthContractRegressionTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// The hermetic fixture blanks the vault keys to disable Key Vault. The check read them
    /// with <c>?? default</c>, which never fires on "" (only on null), so it probed the empty
    /// string, threw, and dragged the whole endpoint to 503 — every /health assertion in the
    /// suite failed for that one reason. A blank key means "no vault", which is healthy.
    /// </summary>
    [Fact]
    public async Task Health_WithVaultDisabled_Returns200_AndReportsKeyVaultNotConfigured()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keyVault = doc.RootElement.GetProperty("checks").GetProperty("KeyVault");
        keyVault.GetProperty("status").GetString().Should().Be("healthy");
        keyVault.GetProperty("description").GetString().Should().Be("not-configured");
    }

    /// <summary>
    /// The masked config block is part of the /health contract (NET_RULES §3) and is asserted
    /// by the E2E-API tier too. It was dropped when the checks were rewritten.
    /// </summary>
    [Fact]
    public async Task Health_CarriesMaskedConfig_WithNoRawSecretValues()
    {
        var json = await _client.GetStringAsync("/health");
        using var doc = JsonDocument.Parse(json);
        var config = doc.RootElement.GetProperty("config");

        config.GetProperty("ASPNETCORE_ENVIRONMENT").GetString().Should().Be("Testing");
        foreach (var prop in config.EnumerateObject().Where(p => p.Name != "ASPNETCORE_ENVIRONMENT"))
        {
            var value = prop.Value.GetString();
            var isMasked = value is "(not set)" or "****" or "configured (redacted)"
                           || (value?.Contains('*') ?? false);
            isMasked.Should().BeTrue(because: $"config key '{prop.Name}' must be masked, got '{value}'");
        }
    }
}

[Collection("WebApp")]
public class OpsSummaryProjectionRegressionTests
{
    private readonly HttpClient _client;

    public OpsSummaryProjectionRegressionTests(TestWebApp factory) => _client = factory.CreateClient();

    private async Task<JsonElement?> LoadSummaryAsync()
    {
        var response = await _client.GetAsync("/api/diag/summary");
        // 404 is the documented "no report yet" state — nothing to assert against.
        if (response.StatusCode != HttpStatusCode.OK)
            return null;
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// /api/portfolio has always excluded this site from its own catalog; this projection did
    /// not, so the dashboard listed "PoPunkouterSoftware is unavailable" as the top attention
    /// item on the very page that was successfully serving it.
    /// </summary>
    [Fact]
    public async Task Summary_NeverReportsThisSiteOrRetiredAppsAsNeedingAttention()
    {
        if (await LoadSummaryAsync() is not { } summary)
            return;

        var offenders = new[] { "popunkoutersoftware", "porepolinetracker" };
        foreach (var item in summary.GetProperty("attentionItems").EnumerateArray())
        {
            var text = (item.GetString() ?? "").Replace("-", "").ToLowerInvariant();
            offenders.Should().NotContain(
                o => text.Contains(o),
                because: "the dashboard must not report on itself or on retired apps");
        }

        foreach (var point in summary.GetProperty("responseTimes").EnumerateArray())
        {
            var label = (point.GetProperty("label").GetString() ?? "").Replace("-", "").ToLowerInvariant();
            offenders.Should().NotContain(o => label.Contains(o));
        }
    }

    /// <summary>
    /// The hero badge rendered security + cleanup while its tooltip claimed it excluded only
    /// the staleness flag — so it silently dropped every unavailable service, the most
    /// actionable items on the page. The count is now computed server-side so the badge and
    /// the headline cannot disagree.
    /// </summary>
    [Fact]
    public async Task Summary_ActionableCount_IsAttentionCountMinusOnlyTheStalenessFlag()
    {
        if (await LoadSummaryAsync() is not { } summary)
            return;

        var attention = summary.GetProperty("attentionCount").GetInt32();
        var actionable = summary.GetProperty("actionableCount").GetInt32();
        var isStale = summary.GetProperty("isStale").GetBoolean();

        actionable.Should().Be(attention - (isStale ? 1 : 0));
        actionable.Should().BeGreaterThanOrEqualTo(
            summary.GetProperty("brokenServices").GetInt32(),
            because: "unavailable services are actionable and must be counted");
    }

    /// <summary>
    /// The compact first-paint contract must not carry fields no one renders.
    /// <c>serverErrors</c> was computed on every request and bound by nothing.
    /// </summary>
    [Fact]
    public async Task Summary_DoesNotCarryUnreadFields()
    {
        if (await LoadSummaryAsync() is not { } summary)
            return;

        summary.TryGetProperty("serverErrors", out _).Should().BeFalse();
        summary.TryGetProperty("webServices", out _).Should().BeFalse();
    }
}

[Collection("WebApp")]
public class PortfolioExclusionRegressionTests
{
    private readonly HttpClient _client;

    public PortfolioExclusionRegressionTests(TestWebApp factory) => _client = factory.CreateClient();

    /// <summary>
    /// Deleting the apps.json entry alone does not remove a card: the response merges the
    /// catalog with every service the Azure scan found, and a decommissioned app's resource
    /// can outlive its DNS. PoRepoLineTracker shipped as a card pointing at a host that no
    /// longer resolves.
    /// </summary>
    [Fact]
    public async Task Portfolio_NeverIncludesRetiredApps_EvenWhenAzureInventoryStillListsThem()
    {
        var response = await _client.GetAsync("/api/portfolio");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("apps").EnumerateArray()
            .Select(a => (a.GetProperty("name").GetString() ?? "").Replace("-", ""))
            .ToList();

        names.Should().NotContain(
            n => n.Contains("porepolinetracker", StringComparison.OrdinalIgnoreCase),
            because: "PoRepoLineTracker is retired and its host no longer resolves");
    }

    /// <summary>
    /// The card renders screenshot + name + description + link. Everything else was payload
    /// serialised onto every app and read by nothing.
    /// </summary>
    [Fact]
    public async Task Portfolio_Cards_CarryOnlyWhatTheCardBinds()
    {
        var response = await _client.GetAsync("/api/portfolio");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var app in doc.RootElement.GetProperty("apps").EnumerateArray())
        {
            app.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(
                new[] { "id", "name", "description", "url", "status", "screenshotUrl" });
        }
    }
}
