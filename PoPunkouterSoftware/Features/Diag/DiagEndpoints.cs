using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PoPunkouterSoftware.Infrastructure.Azure;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Features.Diag;

internal static class DiagEndpoints
{
    static readonly SemaphoreSlim _refreshLock = new(1, 1);

    internal static WebApplication MapDiagEndpoints(this WebApplication app)
    {
        app.MapGet("/api/diag/report", async (IWebHostEnvironment env, AzureReportStore store) =>
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

        app.MapPost("/api/diag/refresh",
            (IServiceScopeFactory scopeFactory, IWebHostEnvironment env, ILogger<Program> logger,
             Microsoft.AspNetCore.SignalR.IHubContext<PoPunkouterSoftware.Features.Azure.RefreshHub> hubCtx) =>
        {
            if (!_refreshLock.Wait(0))
                return Results.Problem(detail: "Refresh already in progress.", statusCode: 409);

            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var azureService = scope.ServiceProvider.GetRequiredService<AzureReportService>();
                var store = scope.ServiceProvider.GetRequiredService<AzureReportStore>();

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var ct = cts.Token;
                var sw = Stopwatch.StartNew();
                var progress = new Progress<(string Step, int Percent)>(p =>
                {
                    _ = hubCtx.Clients.All.SendAsync("RefreshProgress",
                        new { step = p.Step, percent = p.Percent, done = false });
                });
                bool lockReleased = false;
                try
                {
                    var report = await azureService.RunAsync(progress, ct);
                    _refreshLock.Release();
                    lockReleased = true;

                    await store.SaveAsync(report, ct);

                    await hubCtx.Clients.All.SendAsync("RefreshProgress",
                        new { step = "Done", percent = 100, done = true }, ct);

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
                    logger.LogInformation("Azure report refreshed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    if (!lockReleased)
                        _refreshLock.Release();
                    logger.LogWarning("Refresh cancelled or timed out");
                }
                catch (Exception ex)
                {
                    if (!lockReleased)
                        _refreshLock.Release();
                    logger.LogError(ex, "Azure report refresh failed: {Message}", ex.Message);
                }
            });

            return Results.Accepted();
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

        // ── History summary for /timebased time-series charts ─────────────────
        app.MapGet("/api/diag/history", async (AzureReportStore store, CancellationToken ct) =>
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

        // ── Public status page data ───────────────────────────────────────────
        // No auth required — only exposes HTTP status and response time.
        app.MapGet("/api/status", async (AzureReportStore repository, IWebHostEnvironment env, CancellationToken ct) =>
        {
            // Build status page from history (newest first) — up to 30 samples per service
            var historyResult = await repository.LoadHistoryAsync(maxEntries: 30, ct);
            var history = historyResult.IsSuccess ? historyResult.Value ?? new() : new();

            if (history.Count == 0)
            {
                // Fall back to latest report from Table Storage
                var latestResult = await repository.LoadAsync(ct);
                if (latestResult.IsSuccess && latestResult.Value is not null)
                {
                    history.Add(latestResult.Value);
                }
                else
                {
                    // Final fallback: load from local azure-full-report.json file
                    var reportPath = Path.Combine(GetDataDir(env), "azure-full-report.json");
                    if (File.Exists(reportPath))
                    {
                        try
                        {
                            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            var json = await File.ReadAllTextAsync(reportPath, ct);
                            var fileReport = JsonSerializer.Deserialize<AzureReport>(json, opts);
                            if (fileReport is not null)
                                history.Add(fileReport);
                        }
                        catch { /* ignore deserialization errors — return empty report below */ }
                    }
                }
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
