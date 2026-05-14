namespace PoPunkouterSoftware.Features.Diag;

internal static class HealthEndpoints
{
    internal static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        // ─── Health — probes all external connections ──────────────────────────
        app.MapGet("/api/health", async (IHttpClientFactory httpClientFactory, IConfiguration config, IWebHostEnvironment env) =>
        {
            var client = httpClientFactory.CreateClient("health");
            var checks = new Dictionary<string, object>();
            var allHealthy = true;

            // Key Vault reachability
            try
            {
                var kvUriCheck = config["AzureKeyVaultUri"] ?? "https://kv-poshared.vault.azure.net/";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var resp = await client.GetAsync(kvUriCheck, cts.Token);
                // A 401/403 from Key Vault means it's reachable but auth is needed (expected from anonymous ping)
                var kvStatus = resp.StatusCode is >= System.Net.HttpStatusCode.OK and <= System.Net.HttpStatusCode.InternalServerError
                    ? "reachable" : "unreachable";
                if (kvStatus != "reachable")
                    allHealthy = false;
                checks["KeyVault"] = new { status = kvStatus, httpStatus = (int)resp.StatusCode };
            }
            catch (Exception ex)
            {
                allHealthy = false;
                checks["KeyVault"] = new { status = "unreachable", error = ex.Message };
            }

            // Azure Table Storage reachability
            var tableConnStr = config["AzureTableStorage:ConnectionString"];
            var tableEndpoint = config["AzureTableStorage:Endpoint"];
            if (!string.IsNullOrWhiteSpace(tableConnStr) || !string.IsNullOrWhiteSpace(tableEndpoint))
            {
                try
                {
                    // UseDevelopmentStorage=true always wins — probe Azurite directly regardless of any
                    // injected endpoint (e.g. Key Vault overriding the endpoint in dev).
                    string? probeUrl = null;
                    bool isDevStorage = false;
                    if (!string.IsNullOrWhiteSpace(tableConnStr) &&
                        tableConnStr.Equals("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
                    {
                        probeUrl = "http://127.0.0.1:10002/devstoreaccount1";
                        isDevStorage = true;
                    }
                    else
                    {
                        // Prefer explicit endpoint, otherwise parse from connection string.
                        probeUrl = string.IsNullOrWhiteSpace(tableEndpoint) ? null : tableEndpoint;
                        if (probeUrl is null && !string.IsNullOrWhiteSpace(tableConnStr))
                        {
                            // Connection string contains TableEndpoint=https://... or we can build it from AccountName
                            var parts = tableConnStr.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Split('=', 2))
                                .Where(p => p.Length == 2)
                                .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
                            if (parts.TryGetValue("TableEndpoint", out var te))
                                probeUrl = te;
                            else if (parts.TryGetValue("AccountName", out var acct))
                                probeUrl = $"https://{acct}.table.core.windows.net/";
                        }
                    }

                    using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var tsResp = await client.GetAsync(probeUrl, cts2.Token);
                    // 2xx/3xx = healthy. 4xx = service is reachable but auth/config is wrong (degraded).
                    // Exception: Azurite (dev) returns 400 for unauthenticated GET — treat as healthy.
                    // 5xx or network error = unreachable.
                    var (tsStatus, tsHealthy) = (int)tsResp.StatusCode switch
                    {
                        >= 200 and < 400 => ("reachable", true),
                        400 when isDevStorage => ("reachable", true),   // Azurite 400 = running, no auth needed
                        >= 400 and < 500 => ("degraded", false),
                        _ => ("unreachable", false),
                    };
                    if (!tsHealthy)
                        allHealthy = false;
                    checks["TableStorage"] = new { status = tsStatus, httpStatus = (int)tsResp.StatusCode, note = isDevStorage ? "Azurite" : (string?)null };
                }
                catch (Exception ex)
                {
                    allHealthy = false;
                    checks["TableStorage"] = new { status = "unreachable", error = ex.Message };
                }
            }
            else
            {
                checks["TableStorage"] = new { status = "not-configured" };
            }

            return Results.Ok(new
            {
                status = allHealthy ? "healthy" : "degraded",
                application = "PoPunkouterSoftware",
                environment = env.EnvironmentName,
                timestamp = DateTime.UtcNow,
                checks,
                config = new Dictionary<string, string>
                {
                    ["AzureKeyVaultUri"] = MaskValue(config["AzureKeyVaultUri"]),
                    ["AzureTableStorage:ConnectionString"] = MaskValue(config["AzureTableStorage:ConnectionString"]),
                    ["AzureTableStorage:Endpoint"] = MaskValue(config["AzureTableStorage:Endpoint"]),
                    ["ApplicationInsights:ConnectionString"] = MaskValue(config["ApplicationInsights:ConnectionString"]),
                    ["ASPNETCORE_ENVIRONMENT"] = env.EnvironmentName,
                }
            });
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

    internal static string MaskValue(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "(not set)" :
        v.Length <= 8 ? "****" :
        v[..4] + new string('*', Math.Min(v.Length - 8, 20)) + v[^4..];
}
