using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.ResourceManager;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PoPunkouterSoftware;
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
    ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
    ThreadPool.SetMinThreads(
        Math.Max(minWorkerThreads, builder.Configuration.GetValue("ThreadPool:MinWorkerThreads", 100)),
        Math.Max(minCompletionPortThreads, builder.Configuration.GetValue("ThreadPool:MinCompletionPortThreads", 100)));

    // ─── Azure Key Vault — default to shared PoShared vault unless overridden ───
    // Skipped under the "Testing" environment so integration/E2E runs are hermetic
    // and never bind to (or leak secrets from) the real shared vault.
    var kvUriStr = builder.Configuration["KeyVault:Uri"] ?? builder.Configuration["AzureKeyVaultUri"] ?? "https://kv-poshared.vault.azure.net/";
    if (!builder.Environment.IsEnvironment("Testing") && !string.IsNullOrWhiteSpace(kvUriStr))
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
            Log.Warning("Key Vault at {Uri} could not be reached — continuing without it. Reason: {Reason}", kvUriStr, ex.Message);
        }
    }

    // ─── Serilog — structured logging to Console and File ────────────────────
    // App Insights / Azure Monitor telemetry is handled exclusively by OpenTelemetry
    // (Azure.Monitor.OpenTelemetry.AspNetCore). The Serilog.Sinks.ApplicationInsights
    // package was removed to prevent duplicate traces/logs in Application Insights.
    // Connection string is no longer committed to appsettings.json — it is supplied at
    // runtime via the Key Vault secret 'ApplicationInsights--ConnectionString' (mapped to
    // ApplicationInsights:ConnectionString) or the APPLICATIONINSIGHTS_CONNECTION_STRING env var.
    var aiConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
    if (string.IsNullOrWhiteSpace(aiConnectionString))
        aiConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    // CorrelationId enricher requires IHttpContextAccessor
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();
    // writeToProviders: true keeps the OpenTelemetry ILogger provider (registered by
    // UseAzureMonitor below) in the pipeline. Without it, UseSerilog replaces the logger
    // factory and application LOGS silently never reach Application Insights — only
    // traces/metrics do — leaving production logs stranded on the App Service filesystem.
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


    }, writeToProviders: true);

    // ─── .NET 10 TimeProvider abstraction — enables testable time ────────────
    builder.Services.AddSingleton(TimeProvider.System);

    // ─── OpenTelemetry + Azure Monitor (only when connection string is present) ──
    // Custom application metrics (RED on external dependencies + job heartbeats) are
    // defined in PoPunkouterSoftware.Infrastructure.Telemetry and exported by registering
    // the meter source here. They flow to Azure Monitor when a connection string is set;
    // without one they are still recorded in-process (and harmless if never scraped).
    // cloud_RoleName in Azure Monitor is derived from the OpenTelemetry resource's
    // service.name attribute. Without it, traces surface as "unknown_service:dotnet".
    // We resolve it by reflection on the entry assembly so the role always tracks the
    // executing API binary — done here in the API project only (never in the WASM client).
    var serviceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "PoPunkouterSoftware";
    var otelBuilder = builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(serviceName))
        .WithMetrics(m => m.AddMeter(PoPunkouterSoftware.Infrastructure.Telemetry.MeterName))
        // Custom spans (background refresh root + per-step children) — without this source
        // registration every outbound ARM/GitHub dependency recorded during a refresh is an
        // orphaned, unparented span in Azure Monitor.
        .WithTracing(t => t.AddSource(PoPunkouterSoftware.Infrastructure.Telemetry.MeterName));
    if (!string.IsNullOrWhiteSpace(aiConnectionString))
    {
        // ─── Telemetry budgeting (zero-waste) ────────────────────────────────
        // Fixed-rate sampling caps ingestion (and therefore Application Insights
        // cost) at a fraction of total traffic. Tune via the config key without a
        // redeploy; the aggressive 10% default is safe because EXCEPTIONS ARE NOT
        // LOST to it — GlobalExceptionHandler logs every unhandled exception via
        // ILogger, and the Azure Monitor logs pipeline is not trace-sampled, so
        // failures reach Application Insights at 100% while request/dependency
        // spans are sampled.
        var samplingRatio = builder.Configuration.GetValue<float?>("ApplicationInsights:SamplingRatio") ?? 0.10f;
        otelBuilder.UseAzureMonitor(o =>
        {
            o.ConnectionString = aiConnectionString;
            o.SamplingRatio = samplingRatio;
        });

        // Strip recurring noise BEFORE it is sampled/billed: kubernetes-style
        // liveness probes and the masked health endpoint are pure heartbeat traffic,
        // not signal. (499/cancellation request noise is already handled in Serilog.)
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
            o.Filter = ctx =>
            {
                var path = ctx.Request.Path.Value;
                // "/health" prefix covers both /health and /healthz.
                return path is null
                    || !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase);
            });

        // Drop the outbound reachability-probe dependency spans. The "azure-probe"
        // HttpClient exists to test live reachability on a schedule (UA below); it is
        // not an application dependency worth recording on every cycle.
        builder.Services.Configure<HttpClientTraceInstrumentationOptions>(o =>
            o.FilterHttpRequestMessage = req =>
                !req.Headers.UserAgent.ToString().Contains("PoPunkouterSoftware-Audit", StringComparison.OrdinalIgnoreCase));
    }

    // ─── OpenAPI / Scalar ────────────────────────────────────────────
    builder.Services.AddOpenApi();

    builder.WebHost.UseStaticWebAssets();

    builder.Services.AddRazorComponents()
        .AddInteractiveWebAssemblyComponents();

    builder.Services.AddRadzenComponents();
    builder.Services.AddScoped<DialogService>();
    builder.Services.AddScoped<NotificationService>();

    // NOTE: No CORS. This is a single-origin Blazor Web App — the WASM client is
    // served from this same host (see MapStaticAssets + MapRazorComponents below),
    // so there is no cross-origin caller to authorize. Adding CORS here would be
    // dead configuration and needless attack surface.

    // ─── Authentication / Authorization (NET_RULES §3) ───────────────────────
    // FakeAuthHandler reads X-Fake-User / X-Fake-Roles and is registered ONLY
    // outside Production. The handler constructor itself throws on Production
    // initialization — a guardrail against environmental misconfiguration.
    // In Production the scheme is absent, so [Authorize] attributes always
    // fail closed (401 → the management endpoints stay blocked).
    builder.Services
        .AddAuthentication(FakeAuthHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("Management", p => p
            .AddAuthenticationSchemes(FakeAuthHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireRole(FakeAuthHandler.ManagementRole));
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
    // Shared credential and ArmClient registered as Singletons so every service
    // and endpoint reuses the same credential chain walk instead of re-initialising
    // DefaultAzureCredential on every scan or control action.
    builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
    builder.Services.AddSingleton<ArmClient>(sp =>
        new ArmClient(sp.GetRequiredService<TokenCredential>()));

    builder.Services.AddSingleton<AzureReportStore>();
    builder.Services.AddSingleton<AppScreenshotService>();
    builder.Services.AddTransient<AzureReportService>();
    builder.Services.AddTransient<DowntimeDiagnosisService>();
    builder.Services.AddTransient<PlanRecommendationService>();
    builder.Services.AddSingleton<ServicePingerService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServicePingerService>());
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<IncidentService>();
    builder.Services.AddSingleton<RefreshSessionManager>();
    builder.Services.AddSingleton<ReportRefreshRunner>();
    builder.Services.AddSingleton<AiTriageService>();

    // ─── Health checks (NET_RULES §3) ─────────────────────────────────────────
    // One check per external dependency. The /health endpoint renders the
    // HealthReport and the /diag Blazor page renders the same data with
    // masked config values.
    builder.Services.AddHealthChecks()
        .AddCheck<KeyVaultHealthCheck>("KeyVault", tags: new[] { "external", "kv" })
        .AddCheck<TableStorageHealthCheck>("TableStorage", tags: new[] { "external", "storage" })
        .AddCheck<AppInsightsHealthCheck>("ApplicationInsights", tags: new[] { "external", "telemetry" });

    // ─── Response compression for dynamic JSON only ───────────────────────────
    // The report/summary/history payloads are large, highly compressible JSON that
    // Kestrel does not compress by default. .NET 10's MapStaticAssets already serves
    // fingerprinted `.br`/`.gz` blobs from disk with the right Content-Encoding; if
    // UseResponseCompression runs over those the browser hits an integrity/body mismatch
    // ("empty SHA-256 + Failed to fetch" cascade for blazor.boot.json, .wasm, .pdb).
    // Limiting compression to application/json keeps the static-asset pipeline intact
    // while still shrinking every JSON payload.
    builder.Services.AddResponseCompression(o =>
    {
        o.EnableForHttps = true;
        o.MimeTypes = new[] { "application/json" };
    });

    // ─── HTTP client for GitHub API ───────────────────────────────────
    // Resilience: GitHub is a transient-failure-prone dependency we *want* to succeed,
    // so it gets retry + circuit-breaker + timeout. (The "health"/"azure-probe" clients
    // deliberately have NO resilience — their job is to report real reachability, and
    // retries would mask the very outages they exist to detect.)
    //
    // The PAT is bound ONCE here, on the client's default headers. Call sites must never
    // reassign DefaultRequestHeaders on an instance from CreateClient(): handler chains are
    // pooled and shared, so a per-call mutation leaks that credential to every other caller
    // of the same named client. Pass a per-request Authorization header instead.
    var ghPat = builder.Configuration["GitHub:PersonalAccessToken"];
    builder.Services.AddHttpClient("github")
        .ConfigureHttpClient(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware/1.0");
            c.Timeout = TimeSpan.FromSeconds(10);
            if (!string.IsNullOrWhiteSpace(ghPat))
                c.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ghPat);
        })
        .AddStandardResilienceHandler();

    // ─── HTTP client for raw Azure management-plane REST calls ───────────────
    // The ArmClient SDK path has built-in retries, but the direct
    // https://management.azure.com REST calls previously used the unnamed default
    // client with no pipeline at all — a transient 500/429/socket fault produced an
    // immediate gap in the report. Standard resilience = retry + circuit breaker +
    // per-attempt/total timeouts; callers keep their own tighter CancelAfter budgets.
    builder.Services.AddHttpClient("azure-arm")
        .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
        .AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
        });

    // ─── HTTP client for Hugging Face Inference API (cheapest viable AI) ─────
    // Deliberately NO resilience: an AI outage must degrade to "AI summary
    // unavailable", not retry-storm the model and burn the free-tier quota.
    // Mirrors the `health` / `azure-probe` deliberate-no-resilience pattern.
    builder.Services.AddHttpClient(AiTriageService.HttpClientName)
        .ConfigureHttpClient(c =>
        {
            c.BaseAddress = new Uri("https://api-inference.huggingface.co/");
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware/1.0");
        });

    var app = builder.Build();

    app.UseExceptionHandler();

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

            // GlobalExceptionHandler already logs the Error with the full exception; a second
            // Error event here for the same request doubles log volume and muddies triage.
            // Warning keeps failed requests visible in the request log without duplication.
            if (ex is not null || ctx.Response.StatusCode >= 500)
                return LogEventLevel.Warning;
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

    app.UseStaticFiles();
    // Run UseResponseCompression AFTER UseStaticFiles so the fingerprinted asset
    // pipeline (MapStaticAssets, .br / .gz) is not double-compressed. JSON-only policy
    // above is the second guard rail; ordering is the first.
    app.UseResponseCompression();
    app.UseAntiforgery();
    app.UseAuthentication();
    app.UseAuthorization();

    // MapStaticAssets serves compressed + fingerprinted static web assets from the client WASM project.
    // Must be called before MapRazorComponents per framework requirement.
    app.MapStaticAssets();

    app.MapRazorComponents<PoPunkouterSoftware.App>()
       .AddInteractiveWebAssemblyRenderMode()
       .AddAdditionalAssemblies(typeof(PoPunkouterSoftware.Client.MainLayout).Assembly);

    // ─── OpenAPI / Scalar UI ─────────────────────────────────────────
    app.MapOpenApi();
    app.MapScalarApiReference(o =>
    {
        o.Title = "PoPunkouterSoftware API";
        o.Theme = ScalarTheme.Default;
    });

    // ─── Host-level plumbing (not part of any slice) ──────────────────
    app.MapGet("/favicon.ico", () => Results.Redirect("/images/favicon.ico", permanent: false))
        .ExcludeFromDescription();
    app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow:\n", "text/plain"))
        .ExcludeFromDescription();
    app.MapGet("/robots{suffix}.txt", (string suffix) => Results.NoContent())
        .ExcludeFromDescription();

    // ─── Feature slices ───────────────────────────────────────────────
    app.MapConfigEndpoints();
    app.MapHealthEndpoints();
    app.MapDiagEndpoints();
    app.MapPortfolioEndpoints();
    app.MapHub<RefreshHub>("/hubs/refresh");
    app.MapFallback((HttpContext ctx) =>
        Results.NotFound(new
        {
            status = 404,
            path = ctx.Request.Path.Value,
            message = "Route not found."
        }))
        .ExcludeFromDescription();

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

