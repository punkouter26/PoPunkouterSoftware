using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Infrastructure.Azure;
using PoPunkouterSoftware.Infrastructure.Screenshots;

namespace PoPunkouterSoftware.Features.Diag;

/// <summary>
/// Owns the full Azure inventory refresh run (scan → save → incidents → file cache →
/// screenshots → SignalR progress). One shared entry point so both the manual
/// POST /api/diag/refresh and the automatic staleness-triggered rescan execute the
/// identical pipeline under the same <see cref="RefreshSessionManager"/> lock.
/// Register as a singleton.
/// </summary>
internal sealed class ReportRefreshRunner(
    IServiceScopeFactory scopeFactory,
    IWebHostEnvironment env,
    ILogger<ReportRefreshRunner> logger,
    IHubContext<RefreshHub> hubCtx,
    RefreshSessionManager session)
{
    /// <summary>
    /// Auto-triggered runs must not retry-storm when scans keep failing (bad credentials,
    /// ARM throttling): at most one automatic attempt per cooldown window. Manual runs
    /// are never throttled. In-memory only — resets on restart, which is fine.
    /// </summary>
    private static readonly TimeSpan AutoAttemptCooldown = TimeSpan.FromMinutes(30);
    private readonly object _autoGate = new();
    private DateTimeOffset _lastAutoAttemptUtc = DateTimeOffset.MinValue;

    // Hoisted so System.Text.Json's converter metadata cache is reused instead of being
    // rebuilt for every refresh run that writes the file cache.
    private static readonly JsonSerializerOptions FileCacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>True while a refresh run holds the session lock.</summary>
    public bool IsRunning => session.Lock.CurrentCount == 0;

    /// <summary>
    /// Starts a background refresh for the stale-inventory path. Returns false when one is
    /// already running or an automatic attempt happened within the cooldown window.
    /// </summary>
    public bool TryStartAuto()
    {
        lock (_autoGate)
        {
            if (DateTimeOffset.UtcNow - _lastAutoAttemptUtc < AutoAttemptCooldown)
                return false;
            if (IsRunning)
                return false;
            _lastAutoAttemptUtc = DateTimeOffset.UtcNow;
        }
        return TryStart("auto-stale");
    }

    /// <summary>
    /// Starts the refresh pipeline in the background. Returns false (recording a collision
    /// sample) when another run already holds the lock. Fire-and-forget by design: progress
    /// and the terminal outcome are broadcast over the RefreshHub.
    /// </summary>
    public bool TryStart(string trigger)
    {
        // Records one refresh-run sample tagged by outcome (success|failed|cancelled|collision).
        static void RecordOutcome(string outcome) =>
            Telemetry.RefreshRuns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

        if (!session.Lock.Wait(0))
        {
            RecordOutcome("collision");
            logger.LogInformation("Refresh rejected — another refresh is already in progress (trigger: {Trigger}).", trigger);
            return false;
        }

        _ = Task.Run(async () =>
        {
            // The outer try/finally is the only owner of the semaphore: no statement that
            // can throw (scope creation, DI resolution, CTS allocation) runs outside it,
            // so a fault can never leak the lock and dead-lock every future refresh.
            string? terminalError = null;
            var refreshRunId = Guid.NewGuid();
            try
            {
                // Root span so the hundreds of outbound ARM/GitHub dependency calls made by
                // this background run are parented and correlatable in Azure Monitor.
                using var activity = Telemetry.Source.StartActivity("refresh");
                activity?.SetTag("refresh.run_id", refreshRunId);
                activity?.SetTag("refresh.trigger", trigger);
                using var _logScope = Serilog.Context.LogContext.PushProperty("RefreshRunId", refreshRunId);

                using var scope = scopeFactory.CreateScope();
                var azureService = scope.ServiceProvider.GetRequiredService<AzureReportService>();
                var store = scope.ServiceProvider.GetRequiredService<AzureReportStore>();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                session.SetActiveCts(cts);
                try
                {
                    var ct = cts.Token;
                    var sw = Stopwatch.StartNew();
                    var progress = new Progress<(string Step, int Percent)>(p =>
                    {
                        _ = hubCtx.Clients.All.SendAsync("RefreshProgress",
                            new { step = p.Step, percent = p.Percent, done = false });
                    });

                    // Capture the outgoing "latest" report before it is overwritten so incident
                    // detection can diff against it without re-downloading history blobs.
                    var previousResult = await store.LoadAsync(ct);
                    var previousReport = previousResult.IsSuccess ? previousResult.Value : null;

                    var report = await azureService.RunAsync(progress, ct);

                    await store.SaveAsync(report, ct);

                    try
                    {
                        var incidentSvc = scope.ServiceProvider.GetRequiredService<IncidentService>();
                        await incidentSvc.DetectAndRecordAsync(report, previousReport, ct);
                    }
                    catch (Exception iex)
                    {
                        logger.LogWarning(iex, "Incident detection failed (non-fatal)");
                    }

                    var json = JsonSerializer.Serialize(report, FileCacheJsonOptions);
                    var filePath = ReportFileCache.GetReportPath(env);
                    await File.WriteAllTextAsync(filePath, json, ct);

                    // Refresh the home-page screenshots alongside the inventory (non-fatal).
                    try
                    {
                        await hubCtx.Clients.All.SendAsync("RefreshProgress",
                            new { step = "Capturing app screenshots…", percent = 99, done = false }, CancellationToken.None);
                        var screenshots = scope.ServiceProvider.GetRequiredService<AppScreenshotService>();
                        await screenshots.CaptureAsync(AppScreenshotService.ActiveTargets(report), ct);
                    }
                    catch (Exception shotEx)
                    {
                        logger.LogWarning(shotEx, "App screenshot capture failed (non-fatal)");
                    }

                    sw.Stop();
                    RecordOutcome("success");
                    Telemetry.RefreshDuration.Record(sw.Elapsed.TotalMilliseconds);
                    logger.LogInformation("Azure report refreshed in {ElapsedMs}ms (trigger: {Trigger})", sw.ElapsedMilliseconds, trigger);
                }
                finally
                {
                    // Clear the shared reference BEFORE the using disposes the CTS so a
                    // concurrent /cancel-refresh cannot cancel a disposed source.
                    session.SetActiveCts(null);
                }
            }
            catch (OperationCanceledException)
            {
                terminalError = "Refresh cancelled or timed out.";
                RecordOutcome("cancelled");
                logger.LogWarning("Refresh cancelled or timed out");
            }
            catch (Exception ex)
            {
                terminalError = "Refresh failed. Check server logs for details.";
                RecordOutcome("failed");
                logger.LogError(ex, "Azure report refresh failed: {Message}", ex.Message);
            }
            finally
            {
                session.Lock.Release();
                try
                {
                    await hubCtx.Clients.All.SendAsync("RefreshProgress",
                        new { step = terminalError is null ? "Done" : "Failed", percent = 100, done = true, error = terminalError },
                        CancellationToken.None);
                }
                catch (Exception hubEx)
                {
                    logger.LogWarning(hubEx, "Failed to broadcast terminal refresh status");
                }
            }
        });

        return true;
    }
}
