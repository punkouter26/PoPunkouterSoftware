using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Integration;

/// <summary>
/// Tests for the capture-target selection logic in <see cref="AppScreenshotService"/>:
/// scan-derived active services are preferred, and any active entries in the catalog
/// the scan did not see fill in the rest. These two helpers are what let a dev
/// environment render real screenshots without a populated Azure report cache.
/// </summary>
[Collection("WebApp")]
public class AppScreenshotTargetSelectionTests
{
    private readonly TestWebApp _factory;

    public AppScreenshotTargetSelectionTests(TestWebApp factory) => _factory = factory;

    private IWebHostEnvironment Env() =>
        _factory.Services.GetRequiredService<IWebHostEnvironment>();

    [Fact]
    public void CatalogTargets_ReadsActiveEntriesOnlyFromTheOnDiskCatalog()
    {
        var targets = AppScreenshotService.CatalogTargets(Env());

        // Every entry must be an active apps.json row with a parseable URL — the
        // disabled/retired apps are intentionally excluded so capture cannot re-enable
        // a card the operator marked retired by changing an unrelated flag.
        targets.Should().NotBeEmpty("apps.json has at least one active entry");
        foreach (var t in targets)
        {
            t.Host.Should().NotBeNullOrWhiteSpace();
            Uri.TryCreate(t.Url, UriKind.Absolute, out var parsed).Should().BeTrue(t.Url);
        }
        // Disabled entries (e.g. PoSeeReview in apps.json) must be filtered out.
        targets.Should().NotContain(t => t.Url.Contains("poseereview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CombinedTargets_PrefersScanDerivedOverCatalog_WhenBothListTheSameHost()
    {
        // The catalog and the scan may both know about the same host (the curated URL
        // wins for the portfolio response, but for capture the live scan is the
        // authoritative source). A scan-derived URL differs from the catalog URL, so
        // de-dup by host keeps the scan value.
        var report = new AzureReport
        {
            WebServices = new WebServicesInfo
            {
                Services = new List<WebService>
                {
                    new()
                    {
                        Name = "app-polocalcompare",
                        Url = "https://from-scan.example.com/live",
                        HttpStatus = "active",
                        ResourceType = "Microsoft.Web/sites",
                    },
                },
            },
        };

        var combined = AppScreenshotService.CombinedTargets(Env(), report);

        var host = new Uri("https://from-scan.example.com/live").Host.ToLowerInvariant();
        combined.Should().Contain(t => t.Host == host && t.Url == "https://from-scan.example.com/live",
            "a scan-derived URL must win over a catalog URL for the same host");
    }

    [Fact]
    public void CombinedTargets_FillsFromTheCatalog_WhenScanHasNoMatchingService()
    {
        // The catalog has an active entry whose name has no live Microsoft.Web/sites in
        // the (deliberately empty) report. The merge must still surface that host for
        // capture, otherwise dev / fresh-deploy days leave the card blank.
        var report = new AzureReport { WebServices = new WebServicesInfo { Services = [] } };

        var combined = AppScreenshotService.CombinedTargets(Env(), report);

        combined.Should().NotBeEmpty("with no scan the catalog alone is what keeps capture alive");
        // The catalog-only entries must come from apps.json — at least one apps.json
        // active host must appear even when the report carries zero services.
        combined.Select(t => t.Host).Should().IntersectWith(
            AppScreenshotService.CatalogTargets(Env()).Select(t => t.Host));
    }
}
