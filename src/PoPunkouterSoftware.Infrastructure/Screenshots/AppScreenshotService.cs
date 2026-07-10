using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Infrastructure.Screenshots;

/// <summary>
/// Captures mobile-portrait home-page screenshots of the live portfolio apps with headless
/// Chromium and persists them in Azure Blob Storage (container "app-screenshots", one PNG
/// per host). Capture runs during the ops Azure refresh and, at most once per 24 hours,
/// when the home page is loaded with stale screenshots — visitors always get the stored
/// (possibly stale) image immediately; capture never blocks a request.
/// Register as a singleton: it caches the blob container client and serialises capture runs.
/// </summary>
public class AppScreenshotService
{
    private const string ContainerName = "app-screenshots";

    /// <summary>iPhone-class portrait viewport; cards display the PNG at 20% (78×169).</summary>
    private const int ViewportWidth = 390;
    private const int ViewportHeight = 844;

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromMinutes(30);

    private readonly ILogger<AppScreenshotService> _logger;
    private readonly IConfiguration _config;

    private BlobContainerClient? _cachedContainer;
    private readonly SemaphoreSlim _containerLock = new(1, 1);

    // Only one capture run at a time; page loads that find a run in progress just skip.
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    // Throttles retries when capture keeps failing (e.g. Chromium missing on the host),
    // so a hot home page cannot hammer Playwright. In-memory only — resets on restart.
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;

    // Newest stored screenshot; lazily seeded from blob timestamps on first staleness check.
    private DateTimeOffset? _newestCaptureUtc;

    /// <summary>
    /// On Azure App Service the default Playwright install location is the worker's
    /// ephemeral local disk (%USERPROFILE%), which is tiny and shared with Kudu's deploy
    /// extraction — a Chromium download there exhausted it and every deployment failed
    /// with "not enough space on the disk" (2026-07-10). Pin installs to the persistent
    /// HOME share instead so the browser survives restarts and stays off the deploy path.
    /// </summary>
    static AppScreenshotService()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")))
        {
            Environment.SetEnvironmentVariable(
                "PLAYWRIGHT_BROWSERS_PATH", Path.Combine(home, ".playwright-browsers"));
        }
    }

    public AppScreenshotService(ILogger<AppScreenshotService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Kill switch for constrained hosts (F1 quota pressure): screenshots are on by
    /// default and disabled with FeatureFlags:EnableScreenshots=false. Stored images
    /// keep serving either way — only staleness checks and new captures stop.
    /// </summary>
    private bool ScreenshotsEnabled => _config.GetValue("FeatureFlags:EnableScreenshots", true);

    /// <summary>The active (HTTP-reachable) services of a report as capture targets.</summary>
    public static List<(string Host, string Url)> ActiveTargets(AzureReport? report) =>
        (report?.WebServices?.Services ?? new List<WebService>())
            .Where(s => string.Equals(s.HttpStatus, "active", StringComparison.OrdinalIgnoreCase))
            .Select(s => (Host: HostOf(s.Url), s.Url))
            .Where(t => t.Host is not null)
            .Select(t => (t.Host!, t.Url))
            .ToList();

    /// <summary>
    /// True when the newest stored screenshot is older than 24 hours (or none exist) and
    /// no capture attempt was made in the last 30 minutes.
    /// </summary>
    public async Task<bool> IsStaleAsync(CancellationToken ct = default)
    {
        if (!ScreenshotsEnabled)
            return false;

        if (DateTimeOffset.UtcNow - _lastAttemptUtc < AttemptCooldown)
            return false;

        if (_newestCaptureUtc is null)
        {
            var container = await GetContainerAsync(ct);
            if (container is null)
                return false; // storage unavailable — nothing to refresh into

            DateTimeOffset newest = DateTimeOffset.MinValue;
            await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
            {
                if (blob.Properties.LastModified is { } modified && modified > newest)
                    newest = modified;
            }
            _newestCaptureUtc = newest;
        }

        return DateTimeOffset.UtcNow - _newestCaptureUtc > MaxAge;
    }

    /// <summary>Hosts that currently have a stored screenshot.</summary>
    public async Task<HashSet<string>> ListHostsAsync(CancellationToken ct = default)
        => (await ListVersionsAsync(ct)).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stored screenshot hosts and a cache-busting version from blob modification time.</summary>
    public async Task<Dictionary<string, long>> ListVersionsAsync(CancellationToken ct = default)
    {
        var versions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var container = await GetContainerAsync(ct);
        if (container is null)
            return versions;

        try
        {
            await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
            {
                if (blob.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    versions[blob.Name[..^4]] = blob.Properties.LastModified?.ToUnixTimeSeconds() ?? 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list app screenshots");
        }
        return versions;
    }

    /// <summary>Opens a stored screenshot for streaming, or null when missing/unavailable.</summary>
    public async Task<Stream?> OpenReadAsync(string host, CancellationToken ct = default)
    {
        var container = await GetContainerAsync(ct);
        if (container is null)
            return null;

        try
        {
            var blob = container.GetBlobClient($"{host}.png");
            return await blob.ExistsAsync(ct) ? await blob.OpenReadAsync(cancellationToken: ct) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read screenshot for {Host}", host);
            return null;
        }
    }

    /// <summary>
    /// Captures and stores a screenshot per target. Non-fatal throughout: individual app
    /// failures are skipped, and if another capture run is already active this returns
    /// immediately. Safe to fire-and-forget.
    /// </summary>
    public async Task CaptureAsync(IReadOnlyList<(string Host, string Url)> targets, CancellationToken ct = default)
    {
        if (!ScreenshotsEnabled)
        {
            _logger.LogDebug("Screenshot capture skipped — disabled via FeatureFlags:EnableScreenshots");
            return;
        }

        if (targets.Count == 0 || !await _captureLock.WaitAsync(0, ct))
            return;

        try
        {
            _lastAttemptUtc = DateTimeOffset.UtcNow;
            var container = await GetContainerAsync(ct);
            if (container is null)
            {
                _logger.LogWarning("Screenshot capture skipped — blob storage unavailable");
                return;
            }

            using var pw = await CreatePlaywrightWithBrowserAsync();
            if (pw is null)
                return;

            await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            var captured = 0;

            foreach (var (host, url) in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var page = await browser.NewPageAsync(new()
                    {
                        ViewportSize = new() { Width = ViewportWidth, Height = ViewportHeight },
                        IsMobile = true,
                    });
                    try
                    {
                        // Cold Azure apps can take a while; capture whatever rendered on timeout.
                        try
                        {
                            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
                        }
                        catch (System.TimeoutException) { }

                        var png = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png });
                        await container.GetBlobClient($"{host}.png").UploadAsync(
                            new BinaryData(png),
                            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "image/png" } },
                            ct);
                        captured++;
                    }
                    finally
                    {
                        await page.CloseAsync();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Screenshot failed for {Host}", host);
                }
            }

            if (captured > 0)
                _newestCaptureUtc = DateTimeOffset.UtcNow;
            _logger.LogInformation("Captured {Captured}/{Total} app screenshots", captured, targets.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Screenshot capture run failed");
        }
        finally
        {
            _captureLock.Release();
        }
    }

    /// <summary>
    /// Creates Playwright, installing the Chromium headless shell on first use if missing.
    /// Returns null (logged) when the environment cannot run a browser at all.
    /// </summary>
    private async Task<IPlaywright?> CreatePlaywrightWithBrowserAsync()
    {
        var pw = await Playwright.CreateAsync();
        try
        {
            await using var probe = await pw.Chromium.LaunchAsync(new() { Headless = true });
            return pw;
        }
        catch (PlaywrightException)
        {
            // Headless shell only — roughly a third of the full Chromium footprint, which
            // matters on the 1 GB App Service home share. Headless launches use it directly.
            _logger.LogInformation("Chromium not found — installing Playwright headless shell (one-time)");
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium-headless-shell" }));
            if (exitCode == 0)
                return pw;

            pw.Dispose();
            _logger.LogWarning("Playwright browser install failed (exit {ExitCode}) — screenshots disabled", exitCode);
            return null;
        }
    }

    private async Task<BlobContainerClient?> GetContainerAsync(CancellationToken ct)
    {
        if (_cachedContainer is not null)
            return _cachedContainer;

        await _containerLock.WaitAsync(ct);
        try
        {
            if (_cachedContainer is not null)
                return _cachedContainer;

            // Same account as the report table store; a dedicated blob endpoint may be
            // configured for managed-identity setups where only endpoints are known.
            var connectionString = _config["AzureTableStorage:ConnectionString"];
            BlobServiceClient service;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                service = new BlobServiceClient(connectionString);
            }
            else
            {
                var endpoint = _config["AzureBlobStorage:Endpoint"];
                if (string.IsNullOrWhiteSpace(endpoint))
                    return null;
                service = new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential());
            }

            var container = service.GetBlobContainerClient(ContainerName);
            await container.CreateIfNotExistsAsync(cancellationToken: ct);
            _cachedContainer = container;
            return _cachedContainer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Blob storage unavailable — screenshots disabled. Reason: {Reason}", ex.Message);
            return null;
        }
        finally
        {
            _containerLock.Release();
        }
    }

    private static string? HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;
}
