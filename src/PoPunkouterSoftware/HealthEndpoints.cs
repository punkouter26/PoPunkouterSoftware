using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoPunkouterSoftware.Infrastructure;

namespace PoPunkouterSoftware;

internal static class HealthEndpoints
{
    internal static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        // ─── Health — probes all external connections ──────────────────────────
        // NET_RULES §3: "/health .net health check (blazor page that shows all
        // connection status) and /diag. /diag must strictly mask secret values."
        //
        // /health is the deep probe, implemented with the built-in
        // IHealthCheck pipeline (one check per dependency). The Blazor
        // page /diag renders the same report with masked config values.
        // HTTP probes are deliberately NOT retried — they must report
        // real reachability, not retry through outages.
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = HealthResponseWriter.WriteAsync,
        })
        .WithName("GetHealth")
        .WithTags("Health");

        // Lightweight platform probe — does not call external dependencies.
        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            timestamp = DateTime.UtcNow,
        }))
        .WithName("GetLiveness")
        .WithTags("Health");

        return app;
    }
}

/// <summary>
/// Probes the shared Key Vault over HTTPS. Any 2xx-5xx response is treated as
/// "reachable" — a 401/403 is the expected answer from an anonymous ping.
/// </summary>
public sealed class KeyVaultHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public KeyVaultHealthCheck(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = _config["AzureKeyVaultUri"] ?? "https://kv-poshared.vault.azure.net/";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var client = _http.CreateClient("health");
            var resp = await client.GetAsync(uri, cts.Token);
            var data = new Dictionary<string, object> { ["httpStatus"] = (int)resp.StatusCode };
            return resp.StatusCode is >= System.Net.HttpStatusCode.OK and <= System.Net.HttpStatusCode.InternalServerError
                ? HealthCheckResult.Healthy("reachable", data)
                : HealthCheckResult.Unhealthy("unreachable", data: data!);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("unreachable", ex);
        }
    }
}

/// <summary>
/// Probes Azure Table Storage. Azurite returns 400 for unauthenticated GETs
/// in Development — that is treated as "reachable".
/// </summary>
public sealed class TableStorageHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public TableStorageHealthCheck(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connStr = _config["AzureTableStorage:ConnectionString"];
        var endpoint = _config["AzureTableStorage:Endpoint"];
        if (string.IsNullOrWhiteSpace(connStr) && string.IsNullOrWhiteSpace(endpoint))
        {
            return HealthCheckResult.Healthy("not-configured", new Dictionary<string, object>
            {
                ["note"] = "no connection string or endpoint configured",
            });
        }

        string? probeUrl = null;
        var isDevStorage = false;
        if (!string.IsNullOrWhiteSpace(connStr) &&
            connStr.Equals("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
        {
            probeUrl = "http://127.0.0.1:10002/devstoreaccount1";
            isDevStorage = true;
        }
        else
        {
            probeUrl = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint;
            if (probeUrl is null && !string.IsNullOrWhiteSpace(connStr))
            {
                var parts = connStr.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
                if (parts.TryGetValue("TableEndpoint", out var te))
                    probeUrl = te;
                else if (parts.TryGetValue("AccountName", out var acct))
                    probeUrl = $"https://{acct}.table.core.windows.net/";
            }
        }

        if (string.IsNullOrWhiteSpace(probeUrl))
        {
            return HealthCheckResult.Unhealthy("invalid-config", data: new Dictionary<string, object>
            {
                ["error"] = "AzureTableStorage endpoint could not be resolved.",
            });
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var client = _http.CreateClient("health");
            var resp = await client.GetAsync(probeUrl, cts.Token);
            var data = new Dictionary<string, object>
            {
                ["httpStatus"] = (int)resp.StatusCode,
                ["note"] = isDevStorage ? "Azurite" : null!,
            };
            return (int)resp.StatusCode switch
            {
                >= 200 and < 400 => HealthCheckResult.Healthy("reachable", data),
                400 when isDevStorage => HealthCheckResult.Healthy("reachable", data),
                >= 400 and < 500 => HealthCheckResult.Degraded("degraded", data: data),
                _ => HealthCheckResult.Unhealthy("unreachable", data: data),
            };
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("unreachable", ex);
        }
    }
}

/// <summary>
/// Reports the configured Application Insights connection string presence
/// without revealing the value. We never leak the actual string — the
/// InstrumentationKey is a stable per-resource identifier and even the
/// masked prefix reveals region + AI workspace target.
/// </summary>
public sealed class AppInsightsHealthCheck : IHealthCheck
{
    private readonly IConfiguration _config;

    public AppInsightsHealthCheck(IConfiguration config) => _config = config;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connStr = _config["ApplicationInsights:ConnectionString"];
        var present = !string.IsNullOrWhiteSpace(connStr);
        var data = new Dictionary<string, object>
        {
            ["configured"] = present,
            ["value"] = present ? "configured (redacted)" : "(not set)",
        };
        return Task.FromResult(present
            ? HealthCheckResult.Healthy("configured", data)
            : HealthCheckResult.Healthy("not-configured", data));
    }
}

internal static class HealthResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            application = "PoPunkouterSoftware",
            environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>().EnvironmentName,
            timestamp = DateTime.UtcNow,
            checks = report.Entries.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)new
                {
                    status = kvp.Value.Status.ToString().ToLowerInvariant(),
                    description = kvp.Value.Description,
                    data = kvp.Value.Data,
                }),
        };
        await context.Response.WriteAsJsonAsync(payload);
    }
}

