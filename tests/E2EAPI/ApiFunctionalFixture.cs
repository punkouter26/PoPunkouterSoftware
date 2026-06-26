using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace PoPunkouterSoftware.E2EAPI;

/// <summary>
/// Hosts the real application in-process for pure-API end-to-end exercises.
/// Runs in the "Testing" environment so Key Vault and external telemetry stay disabled.
/// </summary>
public class ApiFunctionalApp : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Serilog.Log.Logger = Serilog.Core.Logger.None;

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KeyVault:Uri"] = "",
                ["AzureKeyVaultUri"] = "",
                ["ApplicationInsights:ConnectionString"] = "",
                ["AzureTableStorage:ConnectionString"] = "",
            });
        });
    }
}

[CollectionDefinition("ApiFunctional")]
public class ApiFunctionalCollection : ICollectionFixture<ApiFunctionalApp>;
