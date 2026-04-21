using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using PoPunkouterSoftware.Application.Azure;
using PoPunkouterSoftware.Domain.Azure;
using PoPunkouterSoftware.Features.Azure;
using PoPunkouterSoftware.Features.Diag;
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

    // ─── Azure Key Vault (PoShared) — always loaded when URI is configured ───
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

    // ─── OpenTelemetry + Azure Monitor (only when connection string is present) ──
    var otelBuilder = builder.Services.AddOpenTelemetry();
    if (!string.IsNullOrWhiteSpace(aiConnectionString))
    {
        otelBuilder.UseAzureMonitor(o => o.ConnectionString = aiConnectionString);
    }

    // ─── OpenAPI / Scalar ────────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    builder.WebHost.UseStaticWebAssets();

    builder.Services.AddRazorComponents()
        .AddInteractiveWebAssemblyComponents();

    builder.Services.AddRadzenComponents();
    builder.Services.AddScoped<DialogService>();
    builder.Services.AddScoped<NotificationService>();
    builder.Services.AddScoped<TooltipService>();
    builder.Services.AddScoped<ContextMenuService>();

    // ─── CORS — origins loaded from configuration ─────────────────────────────
    var allowedOrigins = builder.Configuration
        .GetSection("AllowedOrigins")
        .Get<string[]>() ?? [];

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
    });

    // ─── HTTP client used by /health to probe external services ──────────────
    builder.Services.AddHttpClient("health")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

    // ─── HTTP client used by AzureReportService to probe web service URLs ────
    builder.Services.AddHttpClient("azure-probe")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        })
        .ConfigureHttpClient(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PoPunkouterSoftware-Audit/3.0");
        });

    // ─── Azure report analysis + Blob persistence ─────────────────────────────
    // SOLID: Dependency Inversion — register concrete classes against their domain/application interfaces.
    builder.Services.AddSingleton<IAzureReportRepository, AzureReportStore>();
    builder.Services.AddTransient<IAzureReportService, AzureReportService>();
    // Also register concrete types for internal feature use (DiagEndpoints uses AzureReportStore/AzureReportService directly)
    builder.Services.AddSingleton<AzureReportStore>(sp => (AzureReportStore)sp.GetRequiredService<IAzureReportRepository>());
    builder.Services.AddTransient<AzureReportService>(sp => (AzureReportService)sp.GetRequiredService<IAzureReportService>());

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
            diag.Set("UserId",     "anonymous");
            diag.Set("SessionId",  ctx.TraceIdentifier);
            diag.Set("Environment", app.Environment.EnvironmentName);
        };
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

    // ─── OpenAPI / Scalar UI ─────────────────────────────────────────────────
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
                Azure.Data.Tables.TableServiceClient tableService = !string.IsNullOrWhiteSpace(tableConnStr)
                    ? new Azure.Data.Tables.TableServiceClient(tableConnStr)
                    : new Azure.Data.Tables.TableServiceClient(new Uri(tableEndpoint!), new DefaultAzureCredential());

                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tableService.GetPropertiesAsync(cts2.Token);
                checks["TableStorage"] = new { status = "reachable" };
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
                ["AzureKeyVaultUri"]                         = MaskValue(config["AzureKeyVaultUri"]),
                ["AzureTableStorage:ConnectionString"]       = MaskValue(config["AzureTableStorage:ConnectionString"]),
                ["AzureTableStorage:Endpoint"]               = MaskValue(config["AzureTableStorage:Endpoint"]),
                ["ApplicationInsights:ConnectionString"]     = MaskValue(config["ApplicationInsights:ConnectionString"]),
                ["ASPNETCORE_ENVIRONMENT"]                   = env.EnvironmentName,
            }
        });
    };

    app.MapGet("/api/health", healthHandler)
        .WithName("GetHealth")
        .WithTags("Health");

    app.MapGet("/health", healthHandler)
        .WithName("GetHealthRoot")
        .WithTags("Health");

    // ─── Config — lets the client discover the canonical API base URL ─────────
    app.MapGet("/api/config",
        (HttpContext ctx) => Results.Ok(
            new { apiBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api" }))
        .WithName("GetConfig").WithTags("Config");

    // ─── Feature slices ───────────────────────────────────────────────────────
    app.MapDiagEndpoints();

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
