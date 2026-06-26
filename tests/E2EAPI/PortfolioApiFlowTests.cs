using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.E2EAPI;

/// <summary>
/// E2EAPI — pure HTTP calls that emulate how the front-end drives the API, end to end.
/// No browser; this validates the API surface the portfolio UI depends on.
/// </summary>
[Collection("ApiFunctional")]
public class PortfolioApiFlowTests(ApiFunctionalApp app)
{
    private readonly HttpClient _client = app.CreateClient();

    [Fact]
    public async Task Health_ReportsApplicationName()
    {
        var json = await _client.GetStringAsync("/health");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("application").GetString().Should().Be("PoPunkouterSoftware");
        doc.RootElement.TryGetProperty("status", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Liveness_ReturnsOk()
    {
        var resp = await _client.GetAsync("/healthz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Config_DrivesUi_WithoutAnyAuthFlags()
    {
        var json = await _client.GetStringAsync("/api/config");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("apiBase").GetString().Should().EndWith("/api");
        // No-auth site: these flags must not exist.
        doc.RootElement.TryGetProperty("guestLoginEnabled", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("microsoftOAuthEnabled", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GitHubActivity_RejectsMalformedRepo()
    {
        var resp = await _client.GetAsync("/api/github-activity?repo=not-a-valid-repo");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
