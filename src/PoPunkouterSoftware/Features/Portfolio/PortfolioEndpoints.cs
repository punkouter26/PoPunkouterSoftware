using System.Text.Json;
using System.Text.RegularExpressions;
using PoPunkouterSoftware.Features.Diag;
using PoPunkouterSoftware.Infrastructure.Azure;
using PoPunkouterSoftware.Infrastructure.Screenshots;
using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Shared.Portfolio;

namespace PoPunkouterSoftware.Features.Portfolio;

/// <summary>
/// Serves a stable portfolio catalog decorated with live Azure inventory. The catalog
/// remains visible during an outage; services never disappear merely because a probe fails.
/// </summary>
internal static partial class PortfolioEndpoints
{
    /// <summary>
    /// This site itself, which must never appear as a card in its own portfolio.
    ///
    /// <para>Filtering has to happen here rather than by dropping the apps.json entry: the
    /// catalog is only ONE of the two sources merged into the response. Every service the
    /// Azure scan discovers is also shown (that is what keeps broken apps visible), and this
    /// site is a real App Service in the same subscription — so it comes back through the
    /// inventory path no matter what the catalog says.</para>
    /// </summary>
    /// <remarks>
    /// Already in <see cref="NormalizeName"/> form (letters and digits only, lower-cased),
    /// so both the Azure resource name "app-popunkoutersoftware" and the friendly name
    /// "PoPunkouterSoftware" collapse onto an entry here.
    /// </remarks>
    private static readonly HashSet<string> SelfIdentities =
        new(StringComparer.Ordinal) { "apppopunkoutersoftware", "popunkoutersoftware" };

    private static bool IsSelf(params string?[] candidateNames) =>
        candidateNames.Any(n => SelfIdentities.Contains(NormalizeName(n)));

    internal static WebApplication MapPortfolioEndpoints(this WebApplication app)
    {
        var portfolio = app.MapGroup("/api/portfolio").WithTags("Portfolio");

        portfolio.MapGet("", GetPortfolio)
            .WithName("GetPortfolio");

        portfolio.MapGet("/screenshots/{host}", GetScreenshot)
            .WithName("GetPortfolioScreenshot");

        return app;
    }

    internal static async Task<IResult> GetPortfolio(
        IWebHostEnvironment env, IConfiguration config, AzureReportStore store, AppScreenshotService screenshots,
        ReportRefreshRunner refreshRunner, ILogger<Program> logger, CancellationToken ct)
    {
        var (report, services) = await LoadInventoryAsync(env, store, logger, ct);
        var stale = PortfolioFreshness.IsStale(report?.GeneratedAt, DateTime.UtcNow);

        // Self-healing inventory: production has no interactive refresh (management actions
        // are disabled there), so a stale report schedules its own background rescan. Off in
        // Development/Testing — local runs and test suites must never start real ARM scans.
        if (stale && config.GetValue("FeatureFlags:EnableAutoInventoryRefresh", !env.IsDevelopment() && !env.IsEnvironment("Testing")))
        {
            if (refreshRunner.TryStartAuto())
                logger.LogInformation("Inventory is stale — background Azure rescan started");
        }
        var metadata = await LoadMetadataAsync(env, logger, ct);
        var screenshotVersions = await screenshots.ListVersionsAsync(ct);
        var metaByName = metadata
            .GroupBy(m => NormalizeName(m.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => StatusRank(m.Status)).First(), StringComparer.OrdinalIgnoreCase);
        var appsByName = new Dictionary<string, PortfolioApp>(StringComparer.OrdinalIgnoreCase);

        // Catalog entries marked active are the stable showcase, even when Azure inventory is stale or unavailable.
        foreach (var meta in metadata.Where(m => string.Equals(m.Status, "active", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsSelf(meta.Name))
                continue;
            var key = NormalizeName(meta.Name);
            if (appsByName.ContainsKey(key))
                continue;
            appsByName[key] = ToPortfolioApp(meta, null, report?.GeneratedAt, screenshotVersions);
        }

        // Every discovered service is also shown, including broken services, and receives catalog metadata by stable name.
        foreach (var service in services)
        {
            if (IsSelf(service.FriendlyName, service.Name))
                continue;
            var displayName = string.IsNullOrWhiteSpace(service.FriendlyName) ? service.Name : service.FriendlyName;
            var key = NormalizeName(displayName);
            metaByName.TryGetValue(key, out var meta);
            appsByName[key] = ToPortfolioApp(meta, service, report?.GeneratedAt, screenshotVersions);
        }

        var apps = appsByName.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // First page load in 24h with stale screenshots: serve the stored (stale) images
        // now and re-capture in the background for the next visitor. Detached from the
        // request lifetime on purpose — must not die when this response completes.
        if (await screenshots.IsStaleAsync(ct))
        {
            var targets = services
                .Select(s => (Host: HostOf(s.Url), s.Url))
                .Where(t => t.Host is not null)
                .Select(t => (t.Host!, t.Url))
                .ToList();
            _ = Task.Run(async () =>
            {
                try
                {
                    await screenshots.CaptureAsync(targets, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background screenshot refresh failed");
                }
            }, CancellationToken.None);
        }

        return Results.Json(new PortfolioResponse
        {
            GeneratedAt = report?.GeneratedAt,
            Stale = stale,
            RefreshInProgress = refreshRunner.IsRunning,
            // Stable for the lifetime of the server process; lets the client detect
            // "this WASM bundle was built against inventory that is older than the API".
            BuildId = (long)(System.Reflection.Assembly.GetEntryAssembly()?.Location is { } loc && File.Exists(loc)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(loc)).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Apps = apps,
        });
    }

    private static async Task<IResult> GetScreenshot(
        string host, AppScreenshotService screenshots, HttpContext http, CancellationToken ct)
    {
        if (!HostPattern().IsMatch(host))
            return Results.BadRequest(new { error = "Invalid host." });

        var stream = await screenshots.OpenReadAsync(host, ct);
        if (stream is null)
            return Results.NotFound();

        // Screenshots change at most daily; let browsers cache for an hour.
        http.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Stream(stream, "image/png");
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.-]{0,253}$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();

    /// <summary>
    /// Active services from the latest Azure report — same predicate the Ops dashboard
    /// counts as Operational (HttpStatus == "active"). Falls back to the cached report
    /// file when table storage is unavailable; empty when no report exists at all.
    /// </summary>
    private static readonly JsonSerializerOptions FileReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static async Task<(AzureReport? Report, List<WebService> Services)> LoadInventoryAsync(
        IWebHostEnvironment env, AzureReportStore store, ILogger logger, CancellationToken ct)
    {
        AzureReport? report = null;
        var result = await store.LoadAsync(ct);
        if (result.IsSuccess && result.Value is not null)
        {
            report = result.Value;
        }
        else
        {
            var reportPath = ReportFileCache.GetReportPath(env);
            if (File.Exists(reportPath))
            {
                var json = await File.ReadAllTextAsync(reportPath, ct);
                report = ReportFileCache.TryDeserializeReport(json, logger);
            }
        }

        return (report, report?.WebServices?.Services ?? new List<WebService>());
    }

    private static async Task<List<AppMeta>> LoadMetadataAsync(
        IWebHostEnvironment env, ILogger logger, CancellationToken ct)
    {
        var path = Path.Combine(ReportFileCache.GetDataDir(env), "apps.json");
        if (!File.Exists(path))
            return new List<AppMeta>();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var wrapper = JsonSerializer.Deserialize<AppsFile>(json, FileReadJsonOptions);
            return wrapper?.Apps ?? new List<AppMeta>();
        }
        catch (JsonException ex)
        {
            // A malformed catalog file must degrade to "no metadata", never take down the
            // home page — the class contract says the catalog stays visible during outages.
            logger.LogWarning(ex, "apps.json is malformed — serving portfolio without catalog metadata");
            return new List<AppMeta>();
        }
    }

    private static PortfolioApp ToPortfolioApp(
        AppMeta? meta, WebService? service, DateTime? generatedAt, IReadOnlyDictionary<string, long> screenshotVersions)
    {
        var name = meta?.Name ?? service?.FriendlyName ?? service?.Name ?? "Unnamed app";
        // The curated catalog URL wins over the scanned one: inventory can lag reality by
        // hours (or weeks), and a card must never send visitors to a decommissioned host.
        var url = !string.IsNullOrWhiteSpace(meta?.Url) ? meta.Url : service?.Url ?? "";
        var host = HostOf(url);
        long version = 0;
        var hasScreenshot = host is not null && screenshotVersions.TryGetValue(host, out version);
        var status = service is null ? "not-monitored" :
            ServiceHealth.IsHealthy(service.HttpStatus) ? "healthy" : "unavailable";

        return new PortfolioApp
        {
            Id = meta?.Id ?? service?.Name ?? NormalizeName(name),
            Name = name,
            Description = !string.IsNullOrWhiteSpace(meta?.Description) ? meta.Description :
                !string.IsNullOrWhiteSpace(service?.Description) ? service.Description : $"Open {name}.",
            Url = url,
            Category = meta?.Category ?? "app",
            Status = status,
            InventoryGeneratedAt = generatedAt,
            Technologies = meta?.Technologies,
            GithubRepo = meta?.GithubRepo,
            ScreenshotUrl = hasScreenshot ? $"/api/portfolio/screenshots/{host}?v={version}" : null,
        };
    }

    private static string NormalizeName(string? value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static int StatusRank(string? status) => status?.ToLowerInvariant() switch
    {
        "active" => 0,
        "inactive" => 1,
        _ => 2,
    };

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

    private sealed record AppsFile(List<AppMeta>? Apps);

    private sealed record AppMeta(
        string? Id,
        string? Name,
        string? Description,
        string? Category,
        string? Status,
        string? Url,
        List<string>? Technologies,
        string? GithubRepo);
}
