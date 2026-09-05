using System.Text.Json;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.API;

internal static class DiagEndpoints
{
    internal static WebApplication MapDiagEndpoints(this WebApplication app)
    {
        // All machine endpoints share the /api/diag prefix and the "Diag" OpenAPI tag.
        //
        // There is deliberately no bare `/diag` route. One existed: an unauthenticated page
        // that rendered ~130 lines of hand-built HTML (its own dark-only palette, its own
        // breakpoint — a second design system) plus a ?format=json twin. Its only caller was
        // its own smoke test, which is exactly what the "every endpoint needs a consumer" rule
        // in CLAUDE.md exists to catch, and the comments describing it as "the /diag Blazor
        // page" referred to a page this app has never had. It also answered anonymously in
        // Production with the server's absolute ContentRoot path, the environment name, and
        // masked-but-suffixed connection strings. /health (deep probe) and /healthz (liveness)
        // cover everything it was actually used for.
        var diag = app.MapGroup("/api/diag").WithTags("Diag");

        diag.MapGet("/automation-script", (IWebHostEnvironment env) =>
        {
            // The script ships with the API project (CopyToOutputDirectory=PreserveNewest),
            // so in both `dotnet run` and a published deployment it lives at
            // ContentRootPath/Automation/New-AzureEfficiencyReport.ps1. The previous
            // "../..//SCRIPTS/..." fallback existed only because the file lived outside the
            // project tree — that is no longer true.
            var scriptPath = Path.Combine(env.ContentRootPath, "Automation", "New-AzureEfficiencyReport.ps1");

            return File.Exists(scriptPath)
                ? Results.File(
                    scriptPath,
                    contentType: "text/plain; charset=utf-8",
                    fileDownloadName: "New-AzureEfficiencyReport.ps1")
                : Results.Problem(
                    detail: "The Azure efficiency automation script is not available in this deployment.",
                    statusCode: StatusCodes.Status404NotFound);
        })
        .WithName("DownloadAzureEfficiencyAutomationScript");

        // Anonymous by necessity — the Advanced diagnostics panel is WASM with no credential —
        // so the subscription id is masked out of every resource id on the way out. See
        // SecretMasking.MaskSubscriptionIds for why that field and not the rest.
        diag.MapGet("/report", async (HttpContext http, IWebHostEnvironment env, AzureReportStore store, ILogger<Program> logger) =>
        {
            var reportResult = await store.LoadAsync();
            if (reportResult.IsSuccess && reportResult.Value is not null)
            {
                // The report changes at most once per scan — let clients revalidate cheaply.
                var etag = $"\"{reportResult.Value.GeneratedAt?.Ticks ?? 0}\"";
                if (http.Request.Headers.IfNoneMatch.Contains(etag))
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                http.Response.Headers.ETag = etag;
                return MaskedJson(reportResult.Value);
            }

            // Deserialize then re-serialize via Results.Json so ASP.NET Core's camelCase naming
            // policy (JsonSerializerDefaults.Web) is applied consistently. A corrupt cache file
            // must degrade to "no report", not a 500 — this file fallback IS the resilience
            // path when Table Storage is down.
            var fileReport = await ReportFileCache.TryLoadFromFileAsync(env, logger);
            if (fileReport is not null)
                return MaskedJson(fileReport);

            if (!reportResult.IsSuccess)
            {
                return Results.Problem(
                    detail: reportResult.Error ?? "Azure report storage is unavailable and no cached report file exists.",
                    statusCode: 503);
            }

            return Results.Problem(detail: "No report found. Refresh from Azure to generate one.", statusCode: 404);
        });

        diag.MapGet("/summary",async (
            IWebHostEnvironment env, AzureReportStore store, UptimeSampleStore uptimeSamples,
            IConfiguration config, TimeProvider clock, ILogger<Program> logger, CancellationToken ct) =>
        {
            var report = await LoadLatestReportAsync(env, store, logger, ct);
            if (report is null)
                return Results.Problem(detail: "No Azure report is available.", statusCode: 404);

            var now = clock.GetUtcNow();
            var historyResult = await store.LoadHistorySummariesAsync(maxEntries: 30, ct);
            var history = historyResult.IsSuccess ? historyResult.Value ?? new List<HistorySummary>() : new List<HistorySummary>();
            // The pinger's per-day tallies fill the days no full scan covered. LoadRecentAsync
            // never throws — an empty list just means the grid falls back to scans alone.
            var samples = await uptimeSamples.LoadRecentAsync(DashboardInsightsBuilder.UptimeWindowDays, now, ct);
            return Results.Json(BuildOpsSummary(
                report, history, config.GetValue<double?>("Budget:MonthlyUsd"), now.UtcDateTime, samples));
        })
        .WithName("GetOpsSummary");

        diag.MapPost("/refresh", (ReportRefreshRunner runner) =>
            runner.TryStart("manual")
                ? Results.Accepted()
                : Results.Problem(detail: "Refresh already in progress.", statusCode: 409))
        .RequireManagementActions();

        // ── Cancel in-progress refresh ───────────────────────────────────────
        diag.MapPost("/cancel-refresh", (RefreshSessionManager session) =>
        {
            session.Cancel();
            return Results.Ok(new { cancelled = true });
        })
        .RequireManagementActions()
        .WithName("CancelDiagRefresh");

        // ── History summary for /timebased time-series charts ─────────────────
        // Reads the tiny precomputed summary rows written at save time; the previous
        // implementation decompressed and deserialized up to 90 full report blobs per hit.
        diag.MapGet("/history", async (AzureReportStore store, CancellationToken ct) =>
        {
            var result = await store.LoadHistorySummariesAsync(maxEntries: 90, ct);
            if (!result.IsSuccess)
                return Results.Problem(detail: result.Error ?? "Failed to load history", statusCode: 503);

            var summaries = (result.Value ?? new())
                .OrderBy(s => s.GeneratedAt)
                .ToList();

            return Results.Json(summaries);
        });

        // ── AI triage ─────────────────────────────────────────────────────────
        // NET_RULES UPDATE: cheapest viable AI service. The /api/diag/ai
        // endpoint takes a list of attention items and returns a
        // one-paragraph plain-English triage. Disabled by default; the
        // FeatureFlags:EnableAiSummary switch controls availability. The
        // UI never blocks on this — it renders a disabled "AI summary"
        // expander when the feature is off or the upstream model is down.
        //
        // This is the ad-hoc consumer only: the AzureStatusNarrative component's "Rewrite this"
        // button calls this route directly to get a fresh, unpersisted paragraph without a
        // full Azure rescan. The persisted-per-scan summary shown by default on the dashboard
        // is precomputed by ReportRefreshRunner via AiTriageService.GenerateSummaryAsync (which
        // calls AiTriageService.SummarizeAsync in-process, not this HTTP route) and flows
        // through AzureReport.AiSummary / OpsSummary.AiSummary — see AGENT.MD.
        diag.MapPost("/ai", async (AiTriageRequest req, AiTriageService ai, CancellationToken ct) =>
        {
            var result = await ai.SummarizeAsync(req, ct);
            return Results.Json(result);
        })
        .WithName("AiTriage")
        .Produces<AiTriageResult>(StatusCodes.Status200OK);

        // ── Snooze / dismiss a finding ───────────────────────────────────────
        // Findings have no server-side identity — the client synthesizes its own opaque
        // key per finding (e.g. "Reliability|my-app-name") and the server only persists,
        // lists, and expires it. Unprivileged (no .RequireManagementActions()): this is a
        // personal-preference toggle with zero Azure API calls, same tier as POST /api/diag/ai.
        const int MaxSnoozeDurationDays = 90;
        diag.MapPost("/snooze", async (SnoozeRequest req, SnoozeStore store, CancellationToken ct) =>
        {
            if (req.DurationDays <= 0 || req.DurationDays > MaxSnoozeDurationDays)
                return Results.Problem(
                    detail: $"DurationDays must be between 1 and {MaxSnoozeDurationDays}.",
                    statusCode: StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(req.Key))
                return Results.Problem(detail: "Key is required.", statusCode: StatusCodes.Status400BadRequest);

            var expiresAtUtc = DateTimeOffset.UtcNow.AddDays(req.DurationDays);
            var result = await store.UpsertAsync(req.Key, expiresAtUtc, req.Reason, ct);
            if (!result.IsSuccess)
                return Results.Problem(detail: result.Error ?? "Failed to save snooze.", statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Json(result.Value);
        })
        .WithName("SnoozeFinding")
        .Produces<SnoozeEntry>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        diag.MapPost("/snooze/remove", async (SnoozeRemoveRequest req, SnoozeStore store, CancellationToken ct) =>
        {
            // Idempotent by design: un-snoozing an already-gone (or never-snoozed) key is
            // not an error, so the client can call this freely without checking state first.
            await store.RemoveAsync(req.Key, ct);
            return Results.Json(new { removed = true });
        })
        .WithName("RemoveSnooze");

        diag.MapGet("/snoozes", async (SnoozeStore store, CancellationToken ct) =>
        {
            var result = await store.GetActiveAsync(ct);
            if (!result.IsSuccess)
                return Results.Problem(detail: result.Error ?? "Failed to load snoozes.", statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Json(result.Value ?? new List<SnoozeEntry>());
        })
        .WithName("GetActiveSnoozes")
        .Produces<List<SnoozeEntry>>(StatusCodes.Status200OK);

        return app;
    }

    /// <summary>Web-cased JSON, matching what Results.Json would have produced.</summary>
    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes the report with the subscription id masked out of every resource id.
    /// Returns pre-serialized text rather than the object so the mask cannot be skipped by
    /// a future caller reaching for Results.Json out of habit.
    /// </summary>
    private static IResult MaskedJson(AzureReport report) =>
        Results.Text(
            SecretMasking.MaskSubscriptionIds(JsonSerializer.Serialize(report, ReportJsonOptions)),
            "application/json");

    private static async Task<AzureReport?> LoadLatestReportAsync(
        IWebHostEnvironment env, AzureReportStore store, ILogger logger, CancellationToken ct)
    {
        var result = await store.LoadAsync(ct);
        if (result.IsSuccess && result.Value is not null)
            return result.Value;

        return await ReportFileCache.TryLoadFromFileAsync(env, logger, ct);
    }

    private static OpsSummary BuildOpsSummary(
        AzureReport report, IReadOnlyCollection<HistorySummary> history, double? budgetUsd, DateTime utcNow,
        IReadOnlyCollection<UptimeDaySample>? pingSamples = null)
    {
        // The dashboard never reports on the site rendering it, nor on retired apps. Both are
        // real resources in the scanned subscription, so they arrive through the inventory
        // path regardless of the catalog — /api/portfolio has always filtered them and this
        // projection did not, which is how "PoPunkouterSoftware is unavailable" ended up as
        // the first attention item on a page the visitor was successfully reading.
        //
        // Counts are recomputed from the filtered list rather than taken from the report's
        // precomputed ByStatus, which still includes the excluded services. Extracted into
        // AttentionItemsBuilder (Infrastructure) so this read-time projection and
        // ReportRefreshRunner's scan-time AI precompute build the identical list.
        var built = AttentionItemsBuilder.Build(report, PortfolioIdentity.IsSelf);
        var services = built.Services;
        var total = built.Total;
        var active = built.Active;
        var broken = built.Broken;
        var cleanup = built.CleanupCandidates;
        var security = built.SecurityFindings;
        var isStale = built.IsStale;

        return new OpsSummary
        {
            GeneratedAt = report.GeneratedAt,
            IsStale = isStale,
            SubscriptionName = report.Subscription?.Name ?? "Azure",
            TotalServices = total,
            ActiveServices = active,
            BrokenServices = broken,
            HealthPercent = total > 0 ? (int)Math.Round(active * 100d / total) : 100,
            TotalResources = report.AllResourceSummary?.Total ?? 0,
            CostFormatted = report.Cost?.TotalFormatted ?? "$0.00",
            CleanupCandidates = cleanup,
            SecurityFindings = security,
            AttentionCount = broken + security + cleanup + (isStale ? 1 : 0),
            // Everything the operator can actually act on. Staleness is excluded because it
            // is resolved by refreshing, not by fixing a resource; broken services are very
            // much included. The hero badge used to render only security + cleanup while its
            // own tooltip claimed it excluded "the staleness flag itself" — on live data that
            // read "8 item(s) need attention" beside "3 actionable", silently dropping the
            // four unavailable services, the most actionable items on the page.
            ActionableCount = broken + security + cleanup,
            CostDrivers = (report.Cost?.TopCostDrivers ?? new()).Where(x => x.Cost > 0).Take(5)
                .Select(x => new OpsMetricPoint(x.Name, Math.Round(x.Cost, 2))).ToList(),
            ResponseTimes = services.Where(s => s.Connectivity?.ResponseTime > 0)
                .OrderByDescending(s => s.Connectivity!.ResponseTime).Take(6)
                .Select(s => new OpsMetricPoint(s.FriendlyName ?? s.Name, s.Connectivity!.ResponseTime)).ToList(),
            CostHistory = history.Where(h => h.GeneratedAt > DateTime.MinValue && h.TotalCost30Days > 0)
                .OrderBy(h => h.GeneratedAt).TakeLast(30)
                .Select(h => new OpsMetricPoint(h.GeneratedAt.ToString("MMM dd"),
                    Math.Round(h.TotalCost30Days, 2))).ToList(),
            AttentionItems = built.AttentionItems.Take(5).ToList(),
            AiSummary = report.AiSummary,

            // Sparkline series for the hero tiles. Same window and ordering as CostHistory so
            // all four tiles describe the same span of time; the client hides any series with
            // fewer than two points rather than drawing a single dot.
            HealthHistory = TrendSeries(history, h => h.TotalServices > 0
                ? Math.Round(h.ActiveServices * 100d / h.TotalServices)
                : 100),
            BrokenHistory = TrendSeries(history, h => h.BrokenServices),
            ResourceHistory = TrendSeries(history, h => h.TotalResources),

            Changes = DashboardInsightsBuilder.BuildDelta(report, history, PortfolioIdentity.IsSelf),
            Forecast = DashboardInsightsBuilder.BuildForecast(report, history, budgetUsd),
            Uptime = DashboardInsightsBuilder.BuildUptime(
                history, utcNow, PortfolioIdentity.IsSelf,
                DashboardInsightsBuilder.UptimeWindowDays, pingSamples),
        };
    }

    /// <summary>
    /// Projects one numeric field of the history window onto a labelled, oldest-first series.
    /// Rows without a real timestamp are dropped: they would sort to the front and drag every
    /// sparkline down to a phantom zero at its left edge.
    /// </summary>
    private static List<OpsMetricPoint> TrendSeries(
        IReadOnlyCollection<HistorySummary> history, Func<HistorySummary, double> value) =>
        history
            .Where(h => h.GeneratedAt > DateTime.MinValue)
            .OrderBy(h => h.GeneratedAt)
            .TakeLast(30)
            .Select(h => new OpsMetricPoint(h.GeneratedAt.ToString("MMM dd"), Math.Round(value(h), 2)))
            .ToList();
}
