using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Infrastructure.Azure;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Features.Diag;

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
                env.ContentRootPath, "..", "SCRIPTS", "New-AzureEfficiencyReport.ps1"));
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

        diag.MapGet("/report", async (IWebHostEnvironment env, AzureReportStore store) =>
        {
            var reportResult = await store.LoadAsync();
            if (reportResult.IsSuccess && reportResult.Value is not null)
            {
                return Results.Json(reportResult.Value);
            }

            var reportPath = Path.Combine(GetDataDir(env), "azure-full-report.json");
            if (File.Exists(reportPath))
            {
                var json = await File.ReadAllTextAsync(reportPath);
                // Deserialize then re-serialize via Results.Json so ASP.NET Core's camelCase
                // naming policy (JsonSerializerDefaults.Web) is applied consistently.
                var fileOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var fileReport = JsonSerializer.Deserialize<AzureReport>(json, fileOpts);
                return fileReport is not null
                    ? Results.Json(fileReport)
                    : Results.Content(json, "application/json");
            }

            if (!reportResult.IsSuccess)
            {
                return Results.Problem(
                    detail: reportResult.Error ?? "Azure report storage is unavailable and no cached report file exists.",
                    statusCode: 503);
            }

            return Results.Problem(detail: "No report found. Refresh from Azure to generate one.", statusCode: 404);
        });

        diag.MapPost("/refresh",
            (IServiceScopeFactory scopeFactory, IWebHostEnvironment env, ILogger<Program> logger,
             Microsoft.AspNetCore.SignalR.IHubContext<PoPunkouterSoftware.Infrastructure.RefreshHub> hubCtx,
             RefreshSessionManager session) =>
        {
            // Records one refresh-run sample tagged by outcome (success|failed|cancelled|collision).
            static void RecordOutcome(string outcome) =>
                Telemetry.RefreshRuns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

            if (!session.Lock.Wait(0))
            {
                // A refresh is already running — quantify collisions so we know if the lock is
                // a frequent bottleneck rather than a rare race. (question 3)
                RecordOutcome("collision");
                logger.LogInformation("Refresh rejected — another refresh is already in progress (409).");
                return Results.Problem(detail: "Refresh already in progress.", statusCode: 409);
            }

            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var azureService = scope.ServiceProvider.GetRequiredService<AzureReportService>();
                var store = scope.ServiceProvider.GetRequiredService<AzureReportStore>();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                session.SetActiveCts(cts);
                var ct = cts.Token;
                var sw = Stopwatch.StartNew();
                var progress = new Progress<(string Step, int Percent)>(p =>
                {
                    _ = hubCtx.Clients.All.SendAsync("RefreshProgress",
                        new { step = p.Step, percent = p.Percent, done = false });
                });
                string? terminalError = null;
                try
                {
                    var report = await azureService.RunAsync(progress, ct);

                    await store.SaveAsync(report, ct);

                    try
                    {
                        var incidentSvc = scope.ServiceProvider.GetRequiredService<IncidentService>();
                        await incidentSvc.DetectAndRecordAsync(report, ct);
                    }
                    catch (Exception iex)
                    {
                        logger.LogWarning(iex, "Incident detection failed (non-fatal)");
                    }

                    var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
                    var json = JsonSerializer.Serialize(report, opts);
                    var filePath = Path.Combine(GetDataDir(env), "azure-full-report.json");
                    await File.WriteAllTextAsync(filePath, json, ct);

                    sw.Stop();
                    // Metrics: a successful run with its wall-clock duration. (questions 1 & 2)
                    RecordOutcome("success");
                    Telemetry.RefreshDuration.Record(sw.Elapsed.TotalMilliseconds);
                    logger.LogInformation("Azure report refreshed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
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
                    session.SetActiveCts(null);
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

            return Results.Accepted();
        })
        .RequireManagementActions();

        // ── Cancel in-progress refresh ───────────────────────────────────────
        diag.MapPost("/cancel-refresh", (RefreshSessionManager session) =>
        {
            session.Cancel();
            return Results.Ok(new { cancelled = true });
        })
        .RequireManagementActions()
        .WithName("CancelDiagRefresh");

        // ── Az CLI login status ──────────────────────────────────────────────
        diag.MapGet("/az-status", async (HttpContext ctx) =>
        {
            // az.cmd is a batch file on Windows; must run through cmd.exe
            var azExe = OperatingSystem.IsWindows() ? "az.cmd" : "az";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "cmd.exe" : azExe,
                    Arguments = OperatingSystem.IsWindows() ? $"/c {azExe} account show --output json" : $"account show --output json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi)!;
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ctx.RequestAborted);
                var stderrTask = process.StandardError.ReadToEndAsync(ctx.RequestAborted);
                await process.WaitForExitAsync(ctx.RequestAborted);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                    return Results.Ok(new { loggedIn = false, error = stderr.Trim() });

                try
                {
                    var accountData = System.Text.Json.JsonSerializer
                        .Deserialize<System.Text.Json.Nodes.JsonObject>(stdout);
                    return Results.Ok(new { loggedIn = true, account = accountData });
                }
                catch (JsonException)
                {
                    return Results.Ok(new { loggedIn = false, error = "Unexpected az output format." });
                }
            }
            catch (OperationCanceledException)
            {
                return Results.Ok(new { loggedIn = false, error = "Request cancelled." });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { loggedIn = false, error = ex.Message });
            }
        });

        // ── History summary for /timebased time-series charts ─────────────────
        diag.MapGet("/history", async (AzureReportStore store, CancellationToken ct) =>
        {
            var result = await store.LoadHistoryAsync(maxEntries: 90, ct);
            if (!result.IsSuccess)
                return Results.Problem(detail: result.Error ?? "Failed to load history", statusCode: 503);

            var summaries = (result.Value ?? new())
                .Select(r => new PoPunkouterSoftware.Shared.Azure.HistorySummary
                {
                    GeneratedAt = r.GeneratedAt ?? DateTime.MinValue,
                    TotalServices = r.WebServices?.Total ?? 0,
                    ActiveServices = r.WebServices?.ByStatus?.Active ?? 0,
                    BrokenServices = r.WebServices?.ByStatus?.Broken ?? 0,
                    TotalCost30Days = r.Cost?.TotalCost30Days ?? 0,
                    ProjectedMonthCost = r.BurnRate?.ProjectedMonthTotal ?? 0,
                    AvgResponseTimeMs = r.WebServices?.Services?.Where(s => s.Connectivity?.Success == true)
                        .Select(s => (double)(s.Connectivity?.ResponseTime ?? 0))
                        .DefaultIfEmpty(0).Average() ?? 0,
                    Total5xxErrors = r.WebServices?.Services?.Sum(s => s.Metrics7Days?.Http5xx ?? 0) ?? 0,
                    TotalResources = r.AllResourceSummary?.Total ?? 0,
                    ScanDurationMs = r.StepTimings?.Sum(t => t.ElapsedMs) ?? 0,
                    BrokenDelta = r.Delta?.BrokenServicesDelta,
                    Services = (r.WebServices?.Services ?? new()).Select(s => new PoPunkouterSoftware.Shared.Azure.ServiceHistoryPoint
                    {
                        Name = s.FriendlyName ?? s.Name,
                        HttpStatus = s.HttpStatus,
                        ResponseTimeMs = s.Connectivity?.ResponseTime ?? 0,
                        Requests7d = s.Metrics7Days?.Requests ?? 0,
                    }).ToList(),
                })
                .OrderBy(s => s.GeneratedAt)
                .ToList();

            return Results.Json(summaries);
        });

        return app;
    }

    // In the unified Blazor WASM model, wwwroot lives in the Client project.
    // env.WebRootPath is null on the server because the server has no wwwroot of its own.
    // In dev, resolve to the Client project's wwwroot; in production, UseStaticWebAssets()
    // publishes client assets under ContentRootPath/wwwroot, so WebRootPath is non-null.
    private static string GetDataDir(IWebHostEnvironment env) =>
        env.WebRootPath is not null
            ? Path.Combine(env.WebRootPath, "data")
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "PoPunkouterSoftware.Client", "wwwroot", "data"));

    private static async Task<IResult> GetDiag(HttpContext http, IWebHostEnvironment env, IConfiguration config, AzureReportStore store, CancellationToken ct)
    {
        var reportResult = await store.LoadAsync(ct);
        var reportPath = Path.Combine(GetDataDir(env), "azure-full-report.json");
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
                    : HealthEndpoints.MaskValue(pair.Value));
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
