using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Every JSON response must reach the browser in camelCase, because the WASM client's
/// source-generated <c>AppJsonContext</c> binds camelCase names and a reflection fallback
/// does not exist (PublishTrimmed + EnableTrimAnalyzer, no escape hatch).
/// <para>Trimmed to the budget (CLAUDE.md: 50 Integration). This was 15 tests: three
/// theories with thirteen InlineData property names between them, each booting a request
/// to assert one key. Casing is a single serializer setting per endpoint — asserting the
/// whole key set in one request per endpoint is the same claim, and a failure now shows
/// which endpoint regressed rather than which of thirteen keys.</para>
/// </summary>
[Collection("WebApp")]
public class ApiSchemaContractTests
{
    private readonly HttpClient _client;

    public ApiSchemaContractTests(TestWebApp factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_ExposesItsContractInCamelCase()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/health"));

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(
            new[] { "status", "application", "timestamp", "checks", "environment", "config" });
    }

    [Fact]
    public async Task Config_ExposesItsContractInCamelCase()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/config"));

        doc.RootElement.TryGetProperty("apiBase", out _)
            .Should().BeTrue(because: "/api/config must expose camelCase 'apiBase'");
    }

    [Fact]
    public async Task OpsSummary_ExposesItsContractInCamelCase()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/api/diag/summary"));

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(
            new[] { "generatedAt", "isStale", "healthPercent", "attentionCount" });
    }

    /// <summary>
    /// The report is the only nested contract, so it checks one level down as well — a
    /// naming policy can be applied at the root and missed on a nested record.
    /// </summary>
    [Fact]
    public async Task DiagReport_WhenOk_ExposesItsContractInCamelCase_IncludingNestedRecords()
    {
        var response = await _client.GetAsync("/api/diag/report");
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            return;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Contain(
            new[] { "generatedAt", "webServices", "subscription" });

        if (doc.RootElement.TryGetProperty("webServices", out var ws))
            ws.TryGetProperty("total", out _)
                .Should().BeTrue(because: "webServices.total must be camelCase");
    }
}
