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
    private const string GatedEndpoint = "/api/diag/cancel-refresh";

    [Theory]
    [InlineData("/api/diag/refresh")]
    [InlineData("/api/diag/cancel-refresh")]
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
    public async Task FlagEnabled_CancelRefresh_Succeeds()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
        });
        var client = app.CreateClient();

        var resp = await client.PostAsync(GatedEndpoint, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("cancelled").GetBoolean().Should().BeTrue();
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
    public async Task KeyConfigured_WrongHeader_Returns401()
    {
        await using var app = new ManagementGateApp(new()
        {
            ["FeatureFlags:EnableManagementActions"] = "true",
            ["Security:ManagementApiKey"] = "expected-key",
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Management-Key", "wrong-key");

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
}

