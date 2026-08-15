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
        // Mirrors Program.cs's resolution exactly, including its blank check — the probe must
        // report on the vault the app actually bound, not a different one.
        //
        // `?? default` alone was wrong: the hermetic test config sets these keys to "" (not
        // null), so the fallback never fired, GetAsync("") threw, and /health returned 503 for
        // every run under the Testing environment. Program.cs reads a blank value as "no vault"
        // and skips binding it, so a blank value here is healthy-not-configured, not a failure.
        var uri = _config["KeyVault:Uri"] ?? _config["AzureKeyVaultUri"] ?? "https://kv-poshared.vault.azure.net/";
        if (string.IsNullOrWhiteSpace(uri))
        {
            return HealthCheckResult.Healthy("not-configured", new Dictionary<string, object>
            {
                ["note"] = "no Key Vault URI configured",
            });
        }

        try
        {
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
        var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var config = context.RequestServices.GetRequiredService<IConfiguration>();

        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            application = "PoPunkouterSoftware",
            environment = env.EnvironmentName,
            timestamp = DateTime.UtcNow,
            checks = report.Entries.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)new
                {
                    status = kvp.Value.Status.ToString().ToLowerInvariant(),
                    description = kvp.Value.Description,
                    data = kvp.Value.Data,
                }),
            // NET_RULES §3: "/health ... shows all connection status". The masked config
            // block is part of that contract and is asserted by both the integration and
            // E2E-API tiers; it was dropped when the checks were rewritten, which is what
            // left those tests red. Values are masked here exactly as on /diag —
            // ASPNETCORE_ENVIRONMENT is the one deliberately unmasked key.
            config = BuildMaskedConfig(env, config),
        };
        await context.Response.WriteAsJsonAsync(payload);
    }

    private static Dictionary<string, string> BuildMaskedConfig(IWebHostEnvironment env, IConfiguration config) =>
        new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = env.EnvironmentName,
            ["AzureKeyVaultUri"] = SecretMasking.MaskValue(
                config["KeyVault:Uri"] ?? config["AzureKeyVaultUri"]),
            ["AzureTableStorage:ConnectionString"] = SecretMasking.MaskValue(
                config["AzureTableStorage:ConnectionString"]),
            ["AzureTableStorage:Endpoint"] = SecretMasking.MaskValue(
                config["AzureTableStorage:Endpoint"]),
            // Never the masked prefix for App Insights: even four characters of a connection
            // string reveal the region and the target workspace.
            ["ApplicationInsights:ConnectionString"] =
                string.IsNullOrWhiteSpace(config["ApplicationInsights:ConnectionString"])
                    ? "(not set)"
                    : "configured (redacted)",
        };
}

