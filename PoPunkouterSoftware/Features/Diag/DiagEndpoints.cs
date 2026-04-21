using System.Diagnostics;
using System.Text.Json;
using PoPunkouterSoftware.Features.Azure;
using PoShared.Azure;

namespace PoPunkouterSoftware.Features.Diag;

internal static class DiagEndpoints
{

// ─── Route registration ───────────────────────────────────────────────────
    // SOLID: Single Responsibility — this method registers only /api/diag/* routes.
    // GoF:   Extension Method (Decorator variant) — decorates WebApplication without subclassing.
    internal static WebApplication MapDiagEndpoints(this WebApplication app)
    {
        // ── Cached azure-full-report.json (Blob Storage first, file fallback) ──
        app.MapGet("/api/diag/report", async (IWebHostEnvironment env, AzureReportStore store) =>
        {
            // 1. Try Blob Storage
            var report = await store.LoadAsync();
            if (report is not null)
            {
                var json = JsonSerializer.Serialize(report);
                return Results.Content(json, "application/json");
            }

            // 2. Fall back to baked-in JSON file
            var reportPath = Path.Combine(GetDataDir(env), "azure-full-report.json");
            if (!File.Exists(reportPath))
                return Results.NotFound(new { error = "No report found. Click 'Refresh from Azure' to generate one." });

            var bytes = await File.ReadAllBytesAsync(reportPath);
            return Results.Content(System.Text.Encoding.UTF8.GetString(bytes), "application/json");
        });

        // ── Refresh via C# Azure SDK (works locally + on Azure with Managed Identity) ──
        app.MapPost("/api/diag/refresh",
            async (AzureReportService azureService, AzureReportStore store,
                   IWebHostEnvironment env, ILogger<Program> logger,
                   RefreshProgressService progressSvc, HttpContext ctx) =>
        {
            var sw = Stopwatch.StartNew();
            // Use a separate CTS so browser disconnects don't cancel the long-running analysis.
            // Timeout of 10 minutes is a hard ceiling; the analysis normally takes ~2 min.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ct = cts.Token;
            progressSvc.Start();
            var progress = new Progress<(string Step, int Percent)>(p => progressSvc.Report(p.Step, p.Percent));
            try
            {
                var report = await azureService.RunAsync(progress, ct);
                progressSvc.Complete();

                // Save to Blob Storage (if configured)
                await store.SaveAsync(report, ct);

                // Also update the local JSON file (for fallback + local dev)
                var opts      = new JsonSerializerOptions { WriteIndented = true };
                var json      = JsonSerializer.Serialize(report, opts);
                var filePath  = Path.Combine(GetDataDir(env), "azure-full-report.json");
                await File.WriteAllTextAsync(filePath, json, ct);

                // Sync live status + newly discovered apps back into apps.json
                var appsJsonPath = Path.Combine(GetDataDir(env), "apps.json");
                await AppsJsonSyncer.SyncAsync(report, appsJsonPath, ct);

                sw.Stop();
                logger.LogInformation("Azure report refreshed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                return Results.Ok(new { success = true, durationMs = sw.ElapsedMilliseconds });
            }
            catch (OperationCanceledException)
            {
                progressSvc.Fail("Request cancelled or timed out.");
                return Results.Problem(detail: "Request cancelled or timed out.", title: "Cancelled", statusCode: 499);
            }
            catch (Exception ex)
            {
                progressSvc.Fail(ex.Message);
                logger.LogError(ex, "Azure report refresh failed");
                return Results.Problem(ex.Message);
            }
        });

        // ── Refresh progress (polled by the UI during a running refresh) ──────
        app.MapGet("/api/diag/refresh-progress", (RefreshProgressService progressSvc) =>
        {
            var snap = progressSvc.Snapshot();
            return Results.Ok(new
            {
                isRunning = snap.IsRunning,
                step      = snap.Step,
                percent   = snap.Percent,
                log       = snap.Log,
            });
        });

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

}
