using System.Text.Json;
using System.Text.RegularExpressions;
using PoPunkouterSoftware.Features.Diag;
using PoPunkouterSoftware.Infrastructure.Azure;
using PoPunkouterSoftware.Infrastructure.Screenshots;
using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Shared.Portfolio;

namespace PoPunkouterSoftware.Features.Portfolio;

/// <summary>
/// Serves the merged home-page portfolio: one entry per HTTP-active web service in the
/// live Azure inventory, decorated with presentation metadata from apps.json matched by
/// host. Building the list here keeps the client a plain fetch-and-render and keeps the
/// home page count structurally in lockstep with the Ops dashboard's "Operational" total.
/// </summary>
internal static partial class PortfolioEndpoints
{
    internal static WebApplication MapPortfolioEndpoints(this WebApplication app)
    {
        app.MapGet("/api/portfolio", GetPortfolio)
            .WithName("GetPortfolio")
            .WithTags("Portfolio");

        app.MapGet("/api/portfolio/screenshots/{host}", GetScreenshot)
            .WithName("GetPortfolioScreenshot")
            .WithTags("Portfolio");

        return app;
    }

    internal static async Task<IResult> GetPortfolio(
        IWebHostEnvironment env, AzureReportStore store, AppScreenshotService screenshots,
        ILogger<Program> logger, CancellationToken ct)
    {
        var services = await LoadActiveServicesAsync(env, store, ct);
        var metaByHost = await LoadMetadataByHostAsync(env, ct);
        var screenshotHosts = await screenshots.ListHostsAsync(ct);

        var apps = services
            .Select(s =>
            {
                var host = HostOf(s.Url);
                var meta = host is not null && metaByHost.TryGetValue(host, out var m) ? m : null;
                return new PortfolioApp
                {
                    Name = meta?.Name
                        ?? (string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName),
                    Description = meta?.Description ?? "",
                    Url = string.IsNullOrEmpty(s.Url) ? meta?.Url ?? "" : s.Url,
                    Technologies = meta?.Technologies,
                    GithubRepo = meta?.GithubRepo,
                    ScreenshotUrl = host is not null && screenshotHosts.Contains(host)
                        ? $"/api/portfolio/screenshots/{host}"
                        : null,
                };
            })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
    private static async Task<List<WebService>> LoadActiveServicesAsync(
        IWebHostEnvironment env, AzureReportStore store, CancellationToken ct)
    {
        AzureReport? report = null;
        var result = await store.LoadAsync(ct);
        if (result.IsSuccess && result.Value is not null)
        {
            report = result.Value;
        }
        else
        {
            var reportPath = Path.Combine(DiagEndpoints.GetDataDir(env), "azure-full-report.json");
            if (File.Exists(reportPath))
            {
                var json = await File.ReadAllTextAsync(reportPath, ct);
                report = JsonSerializer.Deserialize<AzureReport>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }

        return (report?.WebServices?.Services ?? new List<WebService>())
            .Where(s => string.Equals(s.HttpStatus, "active", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static async Task<Dictionary<string, AppMeta>> LoadMetadataByHostAsync(
        IWebHostEnvironment env, CancellationToken ct)
    {
        var path = Path.Combine(DiagEndpoints.GetDataDir(env), "apps.json");
        if (!File.Exists(path))
            return new Dictionary<string, AppMeta>(StringComparer.OrdinalIgnoreCase);

        var json = await File.ReadAllTextAsync(path, ct);
        var wrapper = JsonSerializer.Deserialize<AppsFile>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return (wrapper?.Apps ?? new List<AppMeta>())
            .Select(a => (host: HostOf(a.Url), app: a))
            .Where(x => x.host is not null)
            .GroupBy(x => x.host!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().app, StringComparer.OrdinalIgnoreCase);
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

    private sealed record AppsFile(List<AppMeta>? Apps);

    private sealed record AppMeta(
        string? Name,
        string? Description,
        string? Url,
        List<string>? Technologies,
        string? GithubRepo);
}
