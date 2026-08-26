using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Integration;

public class TestWebApp : WebApplicationFactory<Program>
{
    /// <summary>
    /// The report-cache file this fixture seeds, so /api/diag/report and /api/diag/summary
    /// have something to project.
    /// <para>These tests used to get a report for free from
    /// <c>Client/wwwroot/data/azure-full-report.json</c> — a committed snapshot of a real
    /// production scan, which was also being served to the public internet as a static file
    /// and has since been removed. The dependency was never declared; blanking
    /// <c>AzureTableStorage:ConnectionString</c> below only looked hermetic because a
    /// checked-in data dump was quietly backfilling it. The fixture now owns its own
    /// fixture data, which is what "hermetic" was supposed to mean.</para>
    /// </summary>
    private string? _seededReportPath;

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

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        SeedReportCache(host);
        return host;
    }

    private void SeedReportCache(IHost host)
    {
        var env = host.Services.GetRequiredService<IWebHostEnvironment>();
        var path = ReportFileCache.EnsureReportPath(env);

        // Never clobber a real cache if one happens to be sitting there.
        if (File.Exists(path))
            return;

        File.WriteAllText(path, JsonSerializer.Serialize(BuildSeedReport(), SeedJsonOptions));
        _seededReportPath = path;
    }

    private static readonly JsonSerializerOptions SeedJsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A deliberately small but REALISTIC report: one healthy service and one broken one, so
    /// the projections under test (health percent, attention items, response times, the
    /// scan delta) all have something non-trivial to compute from. A report of zero services
    /// would make several assertions pass vacuously.
    /// </summary>
    private static AzureReport BuildSeedReport() => new()
    {
        GeneratedAt = DateTime.UtcNow,
        Subscription = new SubscriptionInfo { Name = "TestSubscription" },
        WebServices = new WebServicesInfo
        {
            Total = 2,
            ByStatus = new ByStatusInfo { Active = 1, Broken = 1, Other = 0 },
            Services = new List<WebService>
            {
                new()
                {
                    Name = "app-seed-healthy",
                    FriendlyName = "SeedHealthy",
                    ResourceGroup = "rg-seed",
                    ResourceType = "Microsoft.Web/sites",
                    Url = "https://app-seed-healthy.example.net",
                    HttpStatus = ServiceHealth.Active,
                    PlatformState = "Running",
                    Connectivity = new ConnectivityInfo { Success = true, ResponseTime = 143 },
                },
                new()
                {
                    Name = "app-seed-broken",
                    FriendlyName = "SeedBroken",
                    ResourceGroup = "rg-seed",
                    ResourceType = "Microsoft.Web/sites",
                    Url = "https://app-seed-broken.example.net",
                    HttpStatus = ServiceHealth.Broken,
                    PlatformState = "Running",
                    Connectivity = new ConnectivityInfo { Success = false, ResponseTime = 2100, Error = "503" },
                },
            },
        },
        Cost = new CostInfo
        {
            TotalFormatted = "$12.34",
            TopCostDrivers = new List<CostDriver> { new() { Name = "rg-seed", Cost = 12.34 } },
        },
        AllResourceSummary = new AllResourceSummaryInfo { Total = 7 },
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing && _seededReportPath is not null && File.Exists(_seededReportPath))
        {
            try { File.Delete(_seededReportPath); } catch (IOException) { /* best effort */ }
        }

        base.Dispose(disposing);
    }
}

[CollectionDefinition("WebApp")]
public class WebAppCollection : ICollectionFixture<TestWebApp>
{
}
