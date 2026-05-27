using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.ResourceManager;
using PoPunkouterSoftware;
using PoPunkouterSoftware.Features.Azure;
using PoPunkouterSoftware.Features.Diag;
using PoPunkouterSoftware.Features.GitHub;
using PoPunkouterSoftware.Features.Infra;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Infrastructure.Azure;
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

    // ─── Azure Key Vault — default to shared PoShared vault unless overridden ───
    var kvUriStr = builder.Configuration["KeyVault:Uri"] ?? builder.Configuration["AzureKeyVaultUri"] ?? "https://kv-poshared.vault.azure.net/";
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
            Log.Warning("Key Vault at {Uri} could not be reached — continuing without it. Reason: {Reason}", kvUriStr, ex.Message);
        }
    }

    // ─── Serilog — structured logging to Console and File ────────────────────
    // App Insights / Azure Monitor telemetry is handled exclusively by OpenTelemetry
    // (Azure.Monitor.OpenTelemetry.AspNetCore). The Serilog.Sinks.ApplicationInsights
    // package was removed to prevent duplicate traces/logs in Application Insights.
    var aiConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
    // CorrelationId enricher requires IHttpContextAccessor
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();
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
    builder.Services.AddScoped<PoPunkouterSoftware.Client.PoAppSession>();


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
    // Shared credential and ArmClient registered as Singletons so every service
    // and endpoint reuses the same credential chain walk instead of re-initialising
    // DefaultAzureCredential on every scan or control action.
    builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
    builder.Services.AddSingleton<ArmClient>(sp =>
        new ArmClient(sp.GetRequiredService<TokenCredential>()));

    builder.Services.AddSingleton<AzureReportStore>();
    builder.Services.AddTransient<AzureReportService>();
    builder.Services.AddTransient<DowntimeDiagnosisService>();
    builder.Services.AddSingleton<ServicePingerService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServicePingerService>());
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<IncidentService>();
    builder.Services.AddSingleton<RefreshSessionManager>();

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

    // ─── Config — lets the client discover the canonical API base URL and env mode ─
    // isMockMode=true tells the UI to display the "MOCK DATA" banner (rule 10).
    // Activated when ASPNETCORE_ENVIRONMENT is "Testing" (integration / E2E test runs).
    app.MapGet("/api/config",
        (HttpContext ctx, IWebHostEnvironment env, IConfiguration config) => Results.Ok(new
        {
            apiBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api",
            isMockMode = env.IsEnvironment("Testing"),
            isProduction = env.IsProduction(),
            guestLoginEnabled = !env.IsProduction(),
            microsoftOAuthEnabled = !string.IsNullOrWhiteSpace(config["Authentication:Microsoft:ClientId"]),
            managementActionsEnabled = config.GetValue<bool>("FeatureFlags:EnableManagementActions", env.IsDevelopment() || env.IsEnvironment("Testing")),
            modelCatalog = new
            {
                remote = new[]
                {
                    new { id = "azure-gpt-4o", label = "Azure OpenAI GPT-4o" },
                    new { id = "azure-gpt-4.1-mini", label = "Azure OpenAI GPT-4.1 Mini" }
                },
                browser = new[]
                {
                    new { id = "browser-summarizer", label = "Browser Summarizer" },
                    new { id = "browser-writer", label = "Browser Writer" }
                },
                ollama = new[]
                {
                    new { id = "ollama-llama3.1", label = "Ollama llama3.1" },
                    new { id = "ollama-qwen2.5", label = "Ollama qwen2.5" }
                }
            }
        }))
        .WithName("GetConfig").WithTags("Config");

    // ─── Feature slices ───────────────────────────────────────────────
    app.MapHealthEndpoints();
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
