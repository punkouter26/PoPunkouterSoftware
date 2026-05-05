using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using PoPunkouterSoftware.Application.Azure;
using PoPunkouterSoftware.Domain.Azure;
using PoPunkouterSoftware.Features.Azure;
using PoPunkouterSoftware.Features.Diag;
using PoPunkouterSoftware.Features.GitHub;
using PoPunkouterSoftware.Features.Infra;
using PoPunkouterSoftware.Infrastructure;
using Radzen;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Context;
using Serilog.Events;

// ─── Bootstrap Serilog early so startup errors are captured ──────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Azure Key Vault — always loaded when URI is configured ───
    var kvUriStr = builder.Configuration["AzureKeyVaultUri"];
    if (!string.IsNullOrWhiteSpace(kvUriStr))
    {
        try
        {
            builder.Configuration.AddAzureKeyVault(
                new Uri(kvUriStr),
                new DefaultAzureCredential(),
                new AppKeyVaultSecretManager());
        }
        catch (Exception ex)
        {
            // Non-fatal: Key Vault unavailable without managed identity / az login
            Log.Warning(ex, "Key Vault at {Uri} could not be reached — continuing without it", kvUriStr);
        }
    }

    // ─── Serilog — structured logging to Console, File, App Insights ─────────
    var aiConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
    // CorrelationId enricher requires IHttpContextAccessor
    builder.Services.AddHttpContextAccessor();
    builder.Host.UseSerilog((ctx, services, cfg) =>
    {
        try
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .ReadFrom.Services(services);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Serilog configuration loading failed; falling back to code-based defaults only.");
        }

        var sink = cfg
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithCorrelationId()   // requires Serilog.Enrichers.CorrelationId + IHttpContextAccessor
            .Enrich.WithProperty("Application", "PoPunkouterSoftware")
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/app-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {CorrelationId} {UserId} {Environment} {Message:lj}{NewLine}{Exception}");

        if (!string.IsNullOrWhiteSpace(aiConnectionString))
            sink.WriteTo.ApplicationInsights(aiConnectionString, TelemetryConverter.Traces);
    });

    // ─── .NET 10 TimeProvider abstraction — enables testable time ────────────
    builder.Services.AddSingleton(TimeProvider.System);

    // ─── OpenTelemetry + Azure Monitor (only when connection string is present) ──
    var otelBuilder = builder.Services.AddOpenTelemetry();
    if (!string.IsNullOrWhiteSpace(aiConnectionString))
    {
        otelBuilder.UseAzureMonitor(o => o.ConnectionString = aiConnectionString);
    }

    // ─── OpenAPI / Scalar ────────────────────────────────────────────
    builder.Services.AddOpenApi();

    builder.WebHost.UseStaticWebAssets();

    builder.Services.AddRazorComponents()
        .AddInteractiveWebAssemblyComponents();

    builder.Services.AddRadzenComponents();
    builder.Services.AddScoped<DialogService>();
    builder.Services.AddScoped<NotificationService>();


    // ─── CORS — origins loaded from configuration ─────────────────────
    var allowedOrigins = builder.Configuration
        .GetSection("AllowedOrigins")
        .Get<string[]>() ?? [];

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
    });

    // ─── HTTP clients for Azure services ──────────────────────────────────────
    builder.Services.AddHttpClient("health")
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });

    builder.Services.AddHttpClient("azure-probe")
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware-Audit/3.0");
        });

    // ─── Azure report analysis + Blob persistence ─────────────────────────────

    builder.Services.AddSingleton<IAzureReportRepository, AzureReportStore>();
    builder.Services.AddTransient<IAzureReportService, AzureReportService>();
    builder.Services.AddSingleton<ServicePingerService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServicePingerService>());
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<IncidentService>();

    // ─── In-process memory cache (GitHub activity + AI fix plans) ────────────
    builder.Services.AddMemoryCache();

    // ─── HTTP client for GitHub API ───────────────────────────────────
    var ghPat = builder.Configuration["GitHub:PersonalAccessToken"];
    builder.Services.AddHttpClient("github")
        .ConfigureHttpClient(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware/1.0");
            c.Timeout = TimeSpan.FromSeconds(10);
            if (!string.IsNullOrWhiteSpace(ghPat))
                c.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ghPat);
        });

    // ─── HTTP client for Azure OpenAI ─────────────────────────────────
    builder.Services.AddHttpClient("azure-openai")
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));

    var app = builder.Build();

    // ─── Startup configuration health-checks (non-fatal, informational) ──────
    var startupLog = app.Services.GetRequiredService<ILogger<Program>>();
    if (string.IsNullOrWhiteSpace(builder.Configuration["AzureTableStorage:ConnectionString"]) &&
        string.IsNullOrWhiteSpace(builder.Configuration["AzureTableStorage:Endpoint"]))
        startupLog.LogWarning("AzureTableStorage is not configured — report will use local JSON fallback only.");
    if (string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
        startupLog.LogInformation("ApplicationInsights:ConnectionString is not set — metrics/logs SDK clients will be skipped. Expected in local dev.");

    app.UseSerilogRequestLogging(o =>
    {
        o.EnrichDiagnosticContext = (diag, ctx) =>
        {
            diag.Set("UserId", "anonymous");
            diag.Set("SessionId", ctx.TraceIdentifier);
            diag.Set("Environment", app.Environment.EnvironmentName);
        };
        o.GetLevel = (ctx, _, ex) =>
        {
            // Ignore expected client disconnect noise (499 / aborted requests).
            if (ctx.Response.StatusCode == 499)
                return LogEventLevel.Debug;
            if (ex is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
                return LogEventLevel.Debug;

            if (ex is not null || ctx.Response.StatusCode >= 500)
                return LogEventLevel.Error;
            return LogEventLevel.Information;
        };
    });

    // Convert expected client-aborted request cancellations into a 499 response
    // so they do not surface as unhandled exception telemetry.
    app.Use(async (ctx, next) =>
    {
        try
        {
            await next();
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            if (!ctx.Response.HasStarted)
                ctx.Response.StatusCode = 499;
        }
    });

    app.UseCors();
    app.UseStaticFiles();
    app.UseAntiforgery();

    // MapStaticAssets serves compressed + fingerprinted static web assets from the client WASM project.
    // Must be called before MapRazorComponents per framework requirement.
    app.MapStaticAssets();

    app.MapRazorComponents<PoPunkouterSoftware.App>()
       .AddInteractiveWebAssemblyRenderMode()
       .AddAdditionalAssemblies(typeof(PoPunkouterSoftware.Client.Components.Layout.MainLayout).Assembly);

    // ─── OpenAPI / Scalar UI ─────────────────────────────────────────
    app.MapOpenApi();
    app.MapScalarApiReference(o =>
    {
        o.Title = "PoPunkouterSoftware API";
        o.Theme = ScalarTheme.Default;
    });

    // ─── Health — probes all external connections ─────────────────────────────
    var healthHandler = async (IHttpClientFactory httpClientFactory, IConfiguration config, IWebHostEnvironment env) =>
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
            if (kvStatus != "reachable") allHealthy = false;
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
                if (!tsHealthy) allHealthy = false;
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
    };

    app.MapGet("/api/health", healthHandler)
        .WithName("GetHealth")
        .WithTags("Health");

    app.MapGet("/health", healthHandler)
        .WithName("GetHealthRoot")
        .WithTags("Health");

    // Lightweight platform probe endpoint: does not call external dependencies.
    app.MapGet("/healthz", () => Results.Ok(new
    {
        status = "ok",
        timestamp = DateTime.UtcNow,
    }))
    .WithName("GetLiveness")
    .WithTags("Health");

    // ─── Config — lets the client discover the canonical API base URL and env mode ─
    // isMockMode=true tells the UI to display the "MOCK DATA" banner (rule 10).
    // Activated when ASPNETCORE_ENVIRONMENT is "Testing" (integration / E2E test runs).
    app.MapGet("/api/config",
        (HttpContext ctx, IWebHostEnvironment env) => Results.Ok(new
        {
            apiBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api",
            isMockMode = env.IsEnvironment("Testing"),
        }))
        .WithName("GetConfig").WithTags("Config");

    // ─── Feature slices ───────────────────────────────────────────────
    app.MapDiagEndpoints();
    app.MapGitHubEndpoints();
    app.MapFixPlanEndpoints();
    app.MapInfraEndpoints();
    // New feature endpoints
    app.MapHub<RefreshHub>("/hubs/refresh");
    app.MapPingerEndpoints();
    app.MapAppServiceControlEndpoints();
    app.MapNarrativeEndpoints();
    app.MapIncidentEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "PoPunkouterSoftware terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static string MaskValue(string? v) =>
    string.IsNullOrWhiteSpace(v) ? "(not set)" :
    v.Length <= 8 ? "****" :
    v[..4] + new string('*', Math.Min(v.Length - 8, 20)) + v[^4..];
