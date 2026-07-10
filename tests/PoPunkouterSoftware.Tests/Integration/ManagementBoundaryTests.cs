using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Tests.Integration;

/// <summary>
/// Covers the <c>ManagementActionFilter</c> boundary — the no-auth site's only
/// server-side gate for mutating endpoints (pinger toggle, CI/CD cache bust).
/// Each factory boots with explicit flag/key config so the gate is tested in
/// every state rather than whatever appsettings happens to say.
/// </summary>
public sealed class ManagementGateApp(Dictionary<string, string?>? extra = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
                ["Pinger:Enabled"] = "false",
            };
            foreach (var (key, value) in extra ?? [])
                settings[key] = value;
            cfg.AddInMemoryCollection(settings);
        });
    }
}

public class ManagementActionFilterTests
{
    [Theory]
    [InlineData("/api/pinger/toggle/some-service")]
    [InlineData("/api/infra/cicd-review/refresh")]
    public async Task FlagDisabled_ManagementEndpoints_Return403(string path)
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "false",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync(path, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FlagEnabled_PingerToggle_Succeeds()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync("/api/pinger/toggle/some-service?disable=true", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be("some-service");
        doc.RootElement.GetProperty("disabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task FlagEnabled_CiCdRefresh_ClearsCache()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync("/api/infra/cicd-review/refresh", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("cleared").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task KeyConfigured_MissingHeader_Returns401()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "expected-key",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync("/api/pinger/toggle/some-service", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task KeyConfigured_WrongHeader_Returns401()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "expected-key",
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Management-Key", "wrong-key");

        var resp = await client.PostAsync("/api/pinger/toggle/some-service", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task KeyConfigured_CorrectHeader_Succeeds()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "expected-key",
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Management-Key", "expected-key");

        var resp = await client.PostAsync("/api/pinger/toggle/some-service", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>Read-side pinger contract: status is public (not management-gated).</summary>
public class PingerEndpointTests
{
    [Fact]
    public async Task GetStatus_ReturnsSweptContract()
    {
        await using var app = new ManagementGateApp();
        var client = app.CreateClient();

        var resp = await client.GetAsync("/api/pinger/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("swept", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("results", out var results).Should().BeTrue();
        results.ValueKind.Should().Be(JsonValueKind.Array);
    }
}
