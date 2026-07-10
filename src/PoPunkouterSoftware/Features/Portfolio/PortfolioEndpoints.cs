using System.Text.Json;
using System.Text.RegularExpressions;
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
        IWebHostEnvironment env, AzureReportStore store, AppScreenshotService screenshots,
        ILogger<Program> logger, CancellationToken ct)
    {
        var (report, services) = await LoadInventoryAsync(env, store, logger, ct);
        var metadata = await LoadMetadataAsync(env, logger, ct);
        var screenshotVersions = await screenshots.ListVersionsAsync(ct);
        var metaByName = metadata
            .GroupBy(m => NormalizeName(m.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => StatusRank(m.Status)).First(), StringComparer.OrdinalIgnoreCase);
        var appsByName = new Dictionary<string, PortfolioApp>(StringComparer.OrdinalIgnoreCase);

        // Catalog entries marked active are the stable showcase, even when Azure inventory is stale or unavailable.
        foreach (var meta in metadata.Where(m => string.Equals(m.Status, "active", StringComparison.OrdinalIgnoreCase)))
        {
            var key = NormalizeName(meta.Name);
            if (appsByName.ContainsKey(key))
                continue;
            appsByName[key] = ToPortfolioApp(meta, null, report?.GeneratedAt, screenshotVersions);
        }

        // Every discovered service is also shown, including broken services, and receives catalog metadata by stable name.
        foreach (var service in services)
        {
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

        return Results.Json(apps);
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
        var url = !string.IsNullOrWhiteSpace(service?.Url) ? service.Url : meta?.Url ?? "";
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
