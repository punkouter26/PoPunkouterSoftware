using System.Net;
using System.Text;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.API;

internal static class DiagEndpoints
{
    internal static WebApplication MapDiagEndpoints(this WebApplication app)
    {
        // Human-facing diagnostics page (HTML or JSON) — kept off the /api group.
        app.MapGet("/diag", GetDiag)
        .WithName("GetDiag")
        .WithTags("Diag");

        // All machine endpoints share the /api/diag prefix and the "Diag" OpenAPI tag.
        var diag = app.MapGroup("/api/diag").WithTags("Diag");

        diag.MapGet("/automation-script", (IWebHostEnvironment env) =>
        {
            var publishedPath = Path.Combine(env.ContentRootPath, "Automation", "New-AzureEfficiencyReport.ps1");
            var sourcePath = Path.GetFullPath(Path.Combine(
                env.ContentRootPath, "..", "..", "SCRIPTS", "New-AzureEfficiencyReport.ps1"));
            var scriptPath = File.Exists(publishedPath) ? publishedPath : sourcePath;

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
                return Results.Json(reportResult.Value);
            }

            // Deserialize then re-serialize via Results.Json so ASP.NET Core's camelCase naming
            // policy (JsonSerializerDefaults.Web) is applied consistently. A corrupt cache file
            // must degrade to "no report", not a 500 — this file fallback IS the resilience
            // path when Table Storage is down.
            var fileReport = await ReportFileCache.TryLoadFromFileAsync(env, logger);
            if (fileReport is not null)
                return Results.Json(fileReport);

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
        var built = AttentionItemsBuilder.Build(report, PortfolioIdentity.IsExcluded);
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

            Changes = DashboardInsightsBuilder.BuildDelta(report, history, PortfolioIdentity.IsExcluded),
            Forecast = DashboardInsightsBuilder.BuildForecast(report, history, budgetUsd),
            Uptime = DashboardInsightsBuilder.BuildUptime(
                history, utcNow, PortfolioIdentity.IsExcluded,
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

    private static async Task<IResult> GetDiag(HttpContext http, IWebHostEnvironment env, IConfiguration config, AzureReportStore store, CancellationToken ct)
    {
        var reportResult = await store.LoadAsync(ct);
        var reportPath = ReportFileCache.GetReportPath(env);
        var effectiveKeyVaultUri = config["AzureKeyVaultUri"] ?? "https://kv-poshared.vault.azure.net/";
        var requiredKeys = new Dictionary<string, string?>
        {
            ["AzureKeyVaultUri"] = effectiveKeyVaultUri,
            ["AzureTableStorage:ConnectionString"] = config["AzureTableStorage:ConnectionString"],
            ["ASPNETCORE_ENVIRONMENT"] = env.EnvironmentName,
        };
        var optionalKeys = new Dictionary<string, string?>
        {
            ["AzureTableStorage:Endpoint"] = config["AzureTableStorage:Endpoint"],
            ["ApplicationInsights:ConnectionString"] = config["ApplicationInsights:ConnectionString"],
        };

        var missingRequiredKeys = requiredKeys
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .ToList();
        var optionalMissingKeys = optionalKeys
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .ToList();
        var maskedConfig = requiredKeys
            .Concat(optionalKeys)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Key == "ASPNETCORE_ENVIRONMENT"
                    ? pair.Value ?? "(not set)"
                    : SecretMasking.MaskValue(pair.Value));
        var reportSource = reportResult.IsSuccess && reportResult.Value is not null ? "table-storage" : File.Exists(reportPath) ? "file-cache" : "missing";
        var cachedReportPath = File.Exists(reportPath) ? reportPath : null;
        var reportAvailable = reportResult.IsSuccess && reportResult.Value is not null || File.Exists(reportPath);
        var timestamp = DateTime.UtcNow;

        if (WantsJson(http.Request))
        {
            return Results.Json(new
            {
                status = "ok",
                environment = env.EnvironmentName,
                timestamp,
                missingRequiredKeys,
                optionalMissingKeys,
                config = maskedConfig,
                azureReport = new
                {
                    source = reportSource,
                    cachedReportPath,
                    available = reportAvailable,
                },
            });
        }

        var html = BuildDiagHtml(env.EnvironmentName, timestamp, missingRequiredKeys, optionalMissingKeys, maskedConfig, reportSource, cachedReportPath, reportAvailable);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static bool WantsJson(HttpRequest request)
    {
        if (string.Equals(request.Query["format"], "json", StringComparison.OrdinalIgnoreCase))
            return true;

        return request.Headers.Accept.Any(value =>
            value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string BuildDiagHtml(
        string environment,
        DateTime timestamp,
        IReadOnlyList<string> missingRequiredKeys,
        IReadOnlyList<string> optionalMissingKeys,
        IReadOnlyDictionary<string, string> maskedConfig,
        string reportSource,
        string? cachedReportPath,
        bool reportAvailable)
    {
        static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        var requiredList = missingRequiredKeys.Count == 0
            ? "<li>None</li>"
            : string.Join(string.Empty, missingRequiredKeys.Select(key => $"<li>{Encode(key)}</li>"));
        var optionalList = optionalMissingKeys.Count == 0
            ? "<li>None</li>"
            : string.Join(string.Empty, optionalMissingKeys.Select(key => $"<li>{Encode(key)}</li>"));
        var configRows = string.Join(string.Empty, maskedConfig.Select(pair =>
            $"<tr><th>{Encode(pair.Key)}</th><td>{Encode(pair.Value)}</td></tr>"));
        var statusClass = reportAvailable ? "ok" : "warn";
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\">");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.AppendLine("  <title>PoPunkouterSoftware Diagnostics</title>");
        builder.AppendLine("  <link rel=\"icon\" href=\"/images/favicon.ico\">");
        builder.AppendLine("  <style>");
        builder.AppendLine("    :root { color-scheme: dark; }");
        builder.AppendLine("    body { margin: 0; font-family: Segoe UI, Arial, sans-serif; background: #08121a; color: #edf6fb; }");
        builder.AppendLine("    * { box-sizing: border-box; }");
        builder.AppendLine("    main { max-width: 1040px; margin: 0 auto; padding: 32px 20px 48px; }");
        builder.AppendLine("    h1, h2 { margin: 0 0 12px; }");
        builder.AppendLine("    p { color: #b8d0df; }");
        builder.AppendLine("    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 16px; margin: 24px 0; }");
        builder.AppendLine("    .card { background: rgba(255,255,255,0.04); border: 1px solid rgba(173,196,212,0.2); border-radius: 16px; padding: 18px; box-shadow: 0 16px 36px rgba(0,0,0,0.22); }");
        builder.AppendLine("    .label { display: block; color: #adc4d4; font-size: 0.82rem; text-transform: uppercase; letter-spacing: 0.06em; margin-bottom: 8px; }");
        builder.AppendLine("    .value { font-size: 1.1rem; font-weight: 700; }");
        builder.AppendLine("    .ok { color: #8de2c5; }");
        builder.AppendLine("    .warn { color: #f8c36b; }");
        builder.AppendLine("    table { width: 100%; border-collapse: collapse; margin-top: 8px; }");
        builder.AppendLine("    th, td { text-align: left; padding: 10px 12px; border-bottom: 1px solid rgba(173,196,212,0.16); overflow-wrap: anywhere; word-break: break-word; }");
        builder.AppendLine("    th { width: 34%; color: #adc4d4; font-weight: 600; }");
        builder.AppendLine("    ul { margin: 8px 0 0; padding-left: 18px; }");
        builder.AppendLine("    code { background: rgba(255,255,255,0.06); padding: 2px 6px; border-radius: 6px; overflow-wrap: anywhere; white-space: normal; }");
        builder.AppendLine("    @media (max-width: 640px) { main { padding: 22px 12px 36px; } table, tbody, tr, th, td { display: block; width: 100%; } tr { padding: 8px 0; border-bottom: 1px solid rgba(173,196,212,0.16); } th, td { border-bottom: 0; padding: 4px 0; } th { color: #8de2c5; } .grid { grid-template-columns: minmax(0, 1fr); } }");
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("  <main>");
        builder.AppendLine("    <h1>Diagnostics</h1>");
        builder.AppendLine("    <p>Masked configuration and runtime diagnostics for local validation. Append <code>?format=json</code> for machine-readable output.</p>");
        builder.AppendLine("    <div class=\"grid\">");
        builder.AppendLine("      <section class=\"card\"><span class=\"label\">Environment</span><div class=\"value\">" + Encode(environment) + "</div></section>");
        builder.AppendLine("      <section class=\"card\"><span class=\"label\">Timestamp</span><div class=\"value\">" + Encode(timestamp.ToString("u")) + "</div></section>");
        builder.AppendLine("      <section class=\"card\"><span class=\"label\">Azure Report Source</span><div class=\"value " + statusClass + "\">" + Encode(reportSource) + "</div></section>");
        builder.AppendLine("    </div>");
        builder.AppendLine("    <div class=\"grid\">");
        builder.AppendLine("      <section class=\"card\"><h2>Missing Required Keys</h2><ul>" + requiredList + "</ul></section>");
        builder.AppendLine("      <section class=\"card\"><h2>Missing Optional Keys</h2><ul>" + optionalList + "</ul></section>");
        builder.AppendLine("    </div>");
        builder.AppendLine("    <section class=\"card\"><h2>Config</h2><table><tbody>" + configRows + "</tbody></table></section>");
        builder.AppendLine("    <section class=\"card\" style=\"margin-top:16px;\"><h2>Cache</h2><p>Report available: <strong class=\"" + statusClass + "\">" + Encode(reportAvailable ? "yes" : "no") + "</strong></p><p>Cached report path: <code>" + Encode(cachedReportPath ?? "(none)") + "</code></p></section>");
        builder.AppendLine("  </main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }
}

