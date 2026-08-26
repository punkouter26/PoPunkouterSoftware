using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Covers the <c>ManagementActionFilter</c> boundary — the no-auth site's only
/// server-side gate for mutating endpoints (<c>/api/diag/refresh</c> and
/// <c>/api/diag/cancel-refresh</c>). Each factory boots with explicit flag/key config
/// so the gate is tested in every state rather than whatever appsettings happens to say.
///
/// <para>Success paths drive <c>cancel-refresh</c> rather than <c>refresh</c>: cancelling
/// when nothing is running is a no-op that returns 200 without starting a real Azure scan,
/// so the gate is exercised without any outbound traffic.</para>
/// </summary>
public sealed class ManagementGateApp(Dictionary<string, string?>? extra = null, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment(environment);
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
    private const string GatedEndpoint = "/api/diag/cancel-refresh";

    [Fact]
    public async Task FlagDisabled_ManagementEndpoints_Return403()
    {
        const string path = "/api/diag/refresh";
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "false",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync(path, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

        var resp = await client.PostAsync(GatedEndpoint, content: null);

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

        var resp = await client.PostAsync(GatedEndpoint, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Production fail-closed: flag on, no key ────────────────────────────────
    // Reachable for the first time because the nightly uptime-scan workflow needs
    // EnableManagementActions turned on in Production. Without this guard, that config
    // would leave /api/diag/refresh open to anonymous callers — a free, repeatable,
    // ~30-second Azure subscription scan for anyone who finds the route.

    [Fact]
    public async Task Production_FlagEnabledWithoutKey_Returns403()
    {
        const string path = "/api/diag/refresh";
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "",
        }, environment: "Production");
        var client = app.CreateClient();

        var resp = await client.PostAsync(path, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "a public deployment must not expose an unauthenticated Azure rescan");
    }

    [Fact]
    public async Task Production_FlagEnabledWithKeyAndHeader_Succeeds()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "nightly-key",
        }, environment: "Production");
        var client = app.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, GatedEndpoint);
        request.Headers.Add("X-Management-Key", "nightly-key");
        var resp = await client.SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "this is exactly the call the nightly uptime-scan workflow makes");
    }

    [Fact]
    public async Task NonProduction_FlagEnabledWithoutKey_StillSucceeds()
    {
        // Local development has no key and needs none — the guard is Production-only.
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync(GatedEndpoint, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
