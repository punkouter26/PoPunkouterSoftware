using System.Diagnostics;
using System.Text.Json;
using PoPunkouterSoftware.Features.Azure;
using PoPunkouterSoftware.Shared.Azure;
using PoPunkouterSoftware.Domain.Azure;

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
            var report = await store.LoadAsync();
            if (report is not null)
            {
                return Results.Json(report);
            }

            var reportPath = Path.Combine(GetDataDir(env), "azure-full-report.json");
            if (!File.Exists(reportPath))
                return Results.Problem(detail: "No report found. Refresh from Azure to generate one.", statusCode: 404);

            var json = await File.ReadAllTextAsync(reportPath);
            return Results.Content(json, "application/json");
        });

        // ── Refresh via C# Azure SDK (works locally + on Azure with Managed Identity) ──
        // Fire-and-forget: returns 202 immediately so Azure App Service's 230s request
        // timeout never fires. The client polls /api/diag/refresh-progress until done.
        app.MapPost("/api/diag/refresh",
            (IServiceScopeFactory scopeFactory, RefreshProgressService progressSvc,
             IWebHostEnvironment env, ILogger<Program> logger) =>
        {
            if (progressSvc.IsRunning)
                return Results.Problem(detail: "Refresh already in progress.", statusCode: 409);

            progressSvc.Start();

            // Run in a background task — completely decoupled from the HTTP request lifetime.
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var azureService = scope.ServiceProvider.GetRequiredService<AzureReportService>();
                var store = scope.ServiceProvider.GetRequiredService<AzureReportStore>();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var ct = cts.Token;
                var sw = Stopwatch.StartNew();
                var progress = new Progress<(string Step, int Percent)>(p => progressSvc.Report(p.Step, p.Percent));
                try
                {
                    var report = await azureService.RunAsync(progress, ct);
                    progressSvc.Complete();

                    await store.SaveAsync(report, ct);

                    var opts = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(report, opts);
                    var filePath = Path.Combine(GetDataDir(env), "azure-full-report.json");
                    await File.WriteAllTextAsync(filePath, json, ct);

                    // AppsJsonSyncer removed — apps.json sync no longer performed

                    sw.Stop();
                    logger.LogInformation("Azure report refreshed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    progressSvc.Fail("Request cancelled or timed out.");
                }
                catch (Exception ex)
                {
                    progressSvc.Fail(ex.Message);
                    logger.LogError(ex, "Azure report refresh failed: {Message}", ex.Message);
                }
            });

            return Results.Accepted("/api/diag/refresh-progress", new { started = true });
        });

        // ── Refresh progress (polled by the UI during a running refresh) ──────
        app.MapGet("/api/diag/refresh-progress", (RefreshProgressService progressSvc) =>
        {
            var snap = progressSvc.Snapshot();
            return Results.Ok(new
            {
                isRunning = snap.IsRunning,
                step = snap.Step,
                percent = snap.Percent,
                log = snap.Log,
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

        // ── Public status page data ───────────────────────────────────────────
        // No auth required — only exposes HTTP status and response time.
        app.MapGet("/api/status", async (IAzureReportRepository repository, CancellationToken ct) =>
        {
            // Build status page from history (newest first) — up to 30 samples per service
            var history = await repository.LoadHistoryAsync(maxEntries: 30, ct);

            if (history.Count == 0)
            {
                // Fall back to latest report only
                var latest = await repository.LoadAsync(ct);
                if (latest is not null) history.Add(latest);
            }

            if (history.Count == 0)
                return Results.Ok(new StatusPageReport { GeneratedAt = DateTime.UtcNow });

            // Build per-service entries from history
            var serviceMap = new Dictionary<string, ServiceStatusEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var report in history)
            {
                var reportTime = report.GeneratedAt ?? DateTime.UtcNow;
                foreach (var svc in report.WebServices?.Services ?? new())
                {
                    var sample = new StatusSample
                    {
                        At = reportTime,
                        Status = svc.HttpStatus ?? "unknown",
                        ResponseTimeMs = svc.Connectivity?.ResponseTime > 0 ? svc.Connectivity.ResponseTime : null,
                    };

                    if (serviceMap.TryGetValue(svc.Name, out var existing))
                    {
                        // Append sample — list is already ordered newest first since history is newest first
                        serviceMap[svc.Name] = existing with
                        {
                            Samples = existing.Samples.Append(sample).ToList()
                        };
                    }
                    else
                    {
                        // First occurrence = most recent report = current status
                        serviceMap[svc.Name] = new ServiceStatusEntry
                        {
                            Name = svc.Name,
                            FriendlyName = svc.FriendlyName,
                            Url = svc.Url,
                            CurrentStatus = svc.HttpStatus ?? "unknown",
                            ResponseTimeMs = svc.Connectivity?.ResponseTime > 0 ? svc.Connectivity.ResponseTime : null,
                            Samples = new List<StatusSample> { sample },
                        };
                    }
                }
            }

            var statusReport = new StatusPageReport
            {
                GeneratedAt = history[0].GeneratedAt ?? DateTime.UtcNow,
                Services = serviceMap.Values
                    .OrderByDescending(s => s.CurrentStatus == "active" ? 0 : 1)
                    .ThenBy(s => s.FriendlyName ?? s.Name)
                    .ToList(),
            };

            return Results.Ok(statusReport);
        })
        .WithName("GetStatusPage")
        .WithTags("Status");

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