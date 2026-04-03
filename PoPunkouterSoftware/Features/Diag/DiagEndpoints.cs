using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using PoPunkouterSoftware.Features.Azure;

namespace PoPunkouterSoftware.Features.Diag;

internal static class DiagEndpoints
{
    // ─── In-box .NET 10 metrics (System.Diagnostics.Metrics) ─────────────────
    private static readonly Meter _meter =
        new("PoPunkouterSoftware.Diag", "1.0");

    private static readonly Histogram<double> _scriptDuration =
        _meter.CreateHistogram<double>(
            "diag.script.duration", "ms",
            "Elapsed time for external script executions");

    private static readonly Counter<int> _scriptRuns =
        _meter.CreateCounter<int>(
            "diag.script.runs",
            description: "Total external script invocations, tagged by script name and exit code");

    // ─── Route registration ───────────────────────────────────────────────────
    internal static WebApplication MapDiagEndpoints(this WebApplication app)
    {
        // ── Cached azure-full-report.json (Table Storage first, file fallback) ──
        app.MapGet("/api/diag/report", async (IWebHostEnvironment env, AzureReportStore store) =>
        {
            // 1. Try Table Storage
            var report = await store.LoadAsync();
            if (report is not null)
            {
                var json = JsonSerializer.Serialize(report);
                return Results.Content(json, "application/json");
            }

            // 2. Fall back to baked-in JSON file
            var reportPath = Path.Combine(env.WebRootPath, "data", "azure-full-report.json");
            if (!File.Exists(reportPath))
                return Results.NotFound(new { error = "No report found. Click 'Refresh from Azure' to generate one." });

            var bytes = await File.ReadAllBytesAsync(reportPath);
            return Results.Content(System.Text.Encoding.UTF8.GetString(bytes), "application/json");
        });

        // ── Refresh via C# Azure SDK (works locally + on Azure with Managed Identity) ──
        app.MapPost("/api/diag/refresh",
            async (AzureReportService azureService, AzureReportStore store,
                   IWebHostEnvironment env, ILogger<Program> logger, HttpContext ctx) =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var report = await azureService.RunAsync(ctx.RequestAborted);

                // Save to Table Storage (if configured)
                await store.SaveAsync(report, ctx.RequestAborted);

                // Also update the local JSON file (for fallback + local dev)
                var opts      = new JsonSerializerOptions { WriteIndented = true };
                var json      = JsonSerializer.Serialize(report, opts);
                var filePath  = Path.Combine(env.WebRootPath, "data", "azure-full-report.json");
                await File.WriteAllTextAsync(filePath, json, ctx.RequestAborted);

                // Sync live status + newly discovered apps back into apps.json
                var appsJsonPath = Path.Combine(env.WebRootPath, "data", "apps.json");
                await AppsJsonSyncer.SyncAsync(report, appsJsonPath, ctx.RequestAborted);

                sw.Stop();
                logger.LogInformation("Azure report refreshed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                return Results.Ok(new { success = true, durationMs = sw.ElapsedMilliseconds });
            }
            catch (OperationCanceledException)
            {
                return Results.Problem(detail: "Request cancelled by client.", title: "Cancelled", statusCode: 499);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Azure report refresh failed");
                return Results.Problem(ex.Message);
            }
        });

        // ── Cost audit ───────────────────────────────────────────────────────
        app.MapPost("/api/diag/audit",
            async (IWebHostEnvironment env, ILogger<Program> logger, HttpContext ctx) =>
                await RunNodeScript("scripts/azure-cost-audit.js", "azure-cost-audit-report.json", env, logger, ctx.RequestAborted));

        // ── Spend detail ─────────────────────────────────────────────────────
        app.MapPost("/api/diag/spend",
            async (IWebHostEnvironment env, ILogger<Program> logger, HttpContext ctx) =>
                await RunNodeScript("scripts/azure-spend-detail.js", "azure-spend-detail-report.json", env, logger, ctx.RequestAborted));

        // ── Discover apps ────────────────────────────────────────────────────
        app.MapPost("/api/diag/discover",
            async (IWebHostEnvironment env, ILogger<Program> logger, HttpContext ctx) =>
                await RunNodeScript("scripts/discover-azure-apps.js", "azure-apps-report.json", env, logger, ctx.RequestAborted));

        // ── Az CLI login status ──────────────────────────────────────────────
        app.MapGet("/api/diag/az-status", async (HttpContext ctx) =>
        {
            // az is a .cmd batch on Windows; resolve without cmd.exe wrapper
            var azExe = OperatingSystem.IsWindows() ? "az.cmd" : "az";
            try
            {
                var psi = new ProcessStartInfo(azExe, "account show --output json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                using var process = Process.Start(psi)!;
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ctx.RequestAborted);
                var stderrTask = process.StandardError.ReadToEndAsync(ctx.RequestAborted);
                await process.WaitForExitAsync(ctx.RequestAborted);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                    return Results.Ok(new { loggedIn = false, error = stderr.Trim() });

                System.Text.Json.Nodes.JsonObject? accountData = null;
                try
                {
                    accountData = System.Text.Json.JsonSerializer
                        .Deserialize<System.Text.Json.Nodes.JsonObject>(stdout);
                }
                catch
                {
                    return Results.Ok(new { loggedIn = false, error = "Unexpected az output format." });
                }

                return Results.Json(new { loggedIn = true, account = accountData });
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

        // ── Run azure-diagnostics-standalone.ps1 ─────────────────────────────
        app.MapPost("/api/diag/ps-run",
            async (IWebHostEnvironment env, ILogger<Program> logger, HttpContext ctx) =>
        {
            var workspaceRoot = Path.GetFullPath(Path.Combine(env.WebRootPath, "..", ".."));
            var scriptPath    = Path.Combine(workspaceRoot, "azure-diagnostics-standalone.ps1");

            if (!File.Exists(scriptPath))
                return Results.NotFound(new { error = $"Script not found: {scriptPath}" });

            var jsonOut = Path.Combine(env.WebRootPath, "data", "azure-ps-report.json");
            var htmlOut = Path.Combine(env.WebRootPath, "azure-ps-report.html");
            var sw      = Stopwatch.StartNew();

            try
            {
                var psArgs = $"-NonInteractive -NoProfile -ExecutionPolicy Bypass " +
                             $"-File \"{scriptPath}\" " +
                             $"-OutputFile \"{jsonOut}\" -HtmlOutputFile \"{htmlOut}\"";

                // pwsh resolves to pwsh.exe on Windows and pwsh on Linux/macOS
                var psi = new ProcessStartInfo("pwsh", psArgs)
                {
                    WorkingDirectory       = workspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };

                using var process = Process.Start(psi)!;
                // PS1 can run 3–8 minutes; stream output concurrently with wait
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ctx.RequestAborted);
                var stderrTask = process.StandardError.ReadToEndAsync(ctx.RequestAborted);
                await process.WaitForExitAsync(ctx.RequestAborted);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                sw.Stop();
                RecordScriptMetrics("ps-run", sw, process.ExitCode);

                if (process.ExitCode != 0)
                {
                    logger.LogError("azure-diagnostics-standalone.ps1 failed ({ExitCode}): {Error}",
                        process.ExitCode, stderr);
                    return Results.Problem(detail: stderr, title: "PS1 script failed", statusCode: 500);
                }

                logger.LogInformation(
                    "azure-diagnostics-standalone.ps1 completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                return Results.Ok(new
                {
                    success    = true,
                    jsonReport = "/data/azure-ps-report.json",
                    htmlReport = "/azure-ps-report.html",
                    durationMs = sw.ElapsedMilliseconds,
                });
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("PS1 script run cancelled by client disconnect");
                return Results.Problem(detail: "Request cancelled by client.", title: "Cancelled", statusCode: 499);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to run azure-diagnostics-standalone.ps1");
                return Results.Problem(ex.Message);
            }
        });

        // ── Cached azure-ps-report.json ──────────────────────────────────────
        app.MapGet("/api/diag/ps-report", async (IWebHostEnvironment env) =>
        {
            var reportPath = Path.Combine(env.WebRootPath, "data", "azure-ps-report.json");
            if (!File.Exists(reportPath))
                return Results.NotFound(new { error = "azure-ps-report.json not found. Run /api/diag/ps-run first." });

            var content = await File.ReadAllTextAsync(reportPath);
            return Results.Content(content, "application/json");
        });

        return app;
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private static async Task<IResult> RunNodeScript(
        string relativeScript,
        string? outputFileName,
        IWebHostEnvironment env,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(env.WebRootPath, "..", ".."));
        var scriptPath    = Path.Combine(workspaceRoot, relativeScript);
        var scriptName    = Path.GetFileNameWithoutExtension(relativeScript);

        if (!File.Exists(scriptPath))
            return Results.NotFound(new { error = $"Script not found: {scriptPath}" });

        var sw = Stopwatch.StartNew();
        Process? process = null;

        try
        {
            // Invoke node directly — no cmd.exe wrapper; cross-platform compatible
            var psi = new ProcessStartInfo("node", $"\"{scriptPath}\"")
            {
                WorkingDirectory       = workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            sw.Stop();
            RecordScriptMetrics(scriptName, sw, process.ExitCode);

            if (process.ExitCode != 0)
            {
                logger.LogError("Node script {Script} failed ({ExitCode}): {Error}",
                    scriptName, process.ExitCode, stderr);
                return Results.Problem(detail: stderr, title: "Script failed", statusCode: 500);
            }

            if (outputFileName is not null)
            {
                var outputPath = Path.Combine(workspaceRoot, outputFileName);
                if (File.Exists(outputPath))
                {
                    var content = await File.ReadAllTextAsync(outputPath, cancellationToken);
                    return Results.Content(content, "application/json");
                }
            }

            return Results.Ok(new { success = true, output = stdout, durationMs = sw.ElapsedMilliseconds });
        }
        catch (OperationCanceledException)
        {
            // Kill the orphaned process tree so it does not outlive the HTTP request
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* best-effort cleanup */ }
            }

            logger.LogWarning("Node script {Script} cancelled by client disconnect", scriptName);
            return Results.Problem(detail: "Request cancelled by client.", title: "Cancelled", statusCode: 499);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run {Script}", scriptName);
            return Results.Problem(ex.Message);
        }
    }

    private static void RecordScriptMetrics(string scriptName, Stopwatch sw, int exitCode)
    {
        var tag = new KeyValuePair<string, object?>("script", scriptName);
        _scriptDuration.Record(sw.Elapsed.TotalMilliseconds, tag);
        _scriptRuns.Add(1, tag, new KeyValuePair<string, object?>("exitcode", exitCode));
    }
}
