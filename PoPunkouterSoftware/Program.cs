using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using PoPunkouterSoftware.Features.Diag;
using PoPunkouterSoftware.Infrastructure;
using Radzen;
using Scalar.AspNetCore;
using Serilog;
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
    builder.Host.UseSerilog((ctx, services, cfg) =>
    {
        var sink = cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithCorrelationId()
            .Enrich.WithEnvironmentName()
            .Enrich.WithThreadId()
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

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

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

    // ─── Azure report analysis + Table Storage persistence ────────────────────
    builder.Services.AddSingleton<PoPunkouterSoftware.Features.Azure.AzureReportStore>();
    builder.Services.AddTransient<PoPunkouterSoftware.Features.Azure.AzureReportService>();

    var app = builder.Build();

    // ─── Startup configuration health-checks (non-fatal, informational) ──────
    var startupLog = app.Services.GetRequiredService<ILogger<Program>>();
    if (string.IsNullOrWhiteSpace(builder.Configuration["AzureTableStorage:ConnectionString"]))
        startupLog.LogWarning("AzureTableStorage:ConnectionString is not set — report will use local JSON fallback only. " +
            "For local dev run: dotnet user-secrets set \"AzureTableStorage:ConnectionString\" \"UseDevelopmentStorage=true\"");
    if (string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
        startupLog.LogInformation("ApplicationInsights:ConnectionString is not set — metrics/logs SDK clients will be skipped. Expected in local dev.");

    app.UseSerilogRequestLogging(o =>
    {
        o.EnrichDiagnosticContext = (diag, ctx) =>
        {
            diag.Set("UserId", ctx.User?.Identity?.Name ?? "anonymous");
            diag.Set("Environment", app.Environment.EnvironmentName);
        };
    });

    app.UseCors();
    app.UseStaticFiles();
    app.UseAntiforgery();

    app.MapRazorComponents<PoPunkouterSoftware.App>()
       .AddInteractiveServerRenderMode();

    // ─── OpenAPI / Scalar UI ─────────────────────────────────────────────────
    app.MapOpenApi();
    app.MapScalarApiReference(o =>
    {
        o.Title = "PoPunkouterSoftware API";
        o.Theme = ScalarTheme.Default;
    });

    // ─── Health — probes all external connections ─────────────────────────────
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
            if (kvStatus != "reachable") allHealthy = false;
            checks["KeyVault"] = new { status = kvStatus, httpStatus = (int)resp.StatusCode };
        }
        catch (Exception ex)
        {
            allHealthy = false;
            checks["KeyVault"] = new { status = "unreachable", error = ex.Message };
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
                ["ApplicationInsights:ConnectionString"] = MaskValue(config["ApplicationInsights:ConnectionString"]),
                ["ASPNETCORE_ENVIRONMENT"] = env.EnvironmentName,
            }
        });
    }).WithName("GetHealth").WithTags("Health");

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
