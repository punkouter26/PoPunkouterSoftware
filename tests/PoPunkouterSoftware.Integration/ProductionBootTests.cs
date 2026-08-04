using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Boots the real entry point under <c>Production</c> — the one environment nothing else
/// covered, and the one where the app was broken.
///
/// <para><see cref="FakeAuthHandler"/>'s constructor throws on Production initialization as a
/// deliberate guardrail. That guardrail is only safe if the registration is conditional; it
/// was not. The scheme was added unconditionally AND named as the default, so
/// <c>UseAuthentication</c> resolved it on every single request, the constructor threw, and
/// every page and endpoint answered 500. The comment above the registration claimed the
/// opposite, so nothing looked wrong on inspection.</para>
///
/// <para>Key Vault, Table Storage and App Insights are all blanked: this is about the
/// hosting pipeline, and Program.cs reads a blank vault URI as "do not bind", so no real
/// Azure resource is contacted.</para>
/// </summary>
public sealed class ProductionApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
                ["Pinger:Enabled"] = "false",
            }));
    }
}

public class ProductionBootTests : IClassFixture<ProductionApp>
{
    private readonly HttpClient _client;

    public ProductionBootTests(ProductionApp app) => _client = app.CreateClient();

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/health")]
    [InlineData("/api/config")]
    [InlineData("/api/portfolio")]
    [InlineData("/robots.txt")]
    public async Task PublicEndpoints_DoNotFailWithServerError(string path)
    {
        var response = await _client.GetAsync(path);

        ((int)response.StatusCode).Should().BeLessThan(500,
            because: $"{path} must serve in Production, not throw out of the auth pipeline");
    }

    /// <summary>
    /// With no FakeAuth scheme registered, management actions must still fail closed —
    /// and fail *cleanly*, with a 4xx rather than an unhandled exception.
    /// </summary>
    [Theory]
    [InlineData("/api/diag/refresh")]
    [InlineData("/api/diag/cancel-refresh")]
    public async Task ManagementEndpoints_FailClosed_WithoutServerError(string path)
    {
        var response = await _client.PostAsync(path, content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The X-Fake-* headers must buy nothing in Production. The scheme is absent, so they
    /// are inert request headers — but the response must still be a clean 4xx.
    /// </summary>
    [Fact]
    public async Task FakeAuthHeaders_GrantNothing_InProduction()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/diag/refresh");
        request.Headers.Add(FakeAuthHandler.UserHeader, "admin");
        request.Headers.Add(FakeAuthHandler.RolesHeader, FakeAuthHandler.ManagementRole);

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Impersonation is a FakeAuth affordance and must not exist in Production.</summary>
    [Fact]
    public async Task Impersonate_IsNotMapped_InProduction()
    {
        var response = await _client.PostAsync("/api/impersonate", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
