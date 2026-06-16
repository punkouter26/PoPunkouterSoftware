using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace PoPunkouterSoftware.Tests.Integration;

public class TestWebApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable Key Vault for hermetic tests. Program.cs checks "KeyVault:Uri"
                // BEFORE "AzureKeyVaultUri", so both must be cleared or the real shared
                // vault loads and leaks secrets (e.g. Authentication:Microsoft:ClientId).
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
            });
        });
    }
}

[CollectionDefinition("WebApp")]
public class WebAppCollection : ICollectionFixture<TestWebApp>
{
}