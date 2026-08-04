using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Analyses an Azure subscription using the Azure SDK and DefaultAzureCredential.
/// Works locally (via az login / VS login) and on Azure (via Managed Identity).
/// Produces the same AzureReport structure consumed by AzureDashboard.razor.
///
/// <para>This file holds the orchestrator only. Each scan step lives in a sibling partial
/// named for its concern (.Discovery, .Metrics, .Cost, .Security, .Inventory, .Cleanup,
/// .GitHubCorrelation, .Helpers). The primary constructor is declared here and only here.</para>
/// </summary>
public partial class AzureReportService(
    ILogger<AzureReportService> logger,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env,
    IConfiguration config,
    AzureReportStore repository,
    ArmClient arm,
    TokenCredential credential,
    DowntimeDiagnosisService downtimeDiagnosis,
    PlanRecommendationService planRecommendation)
{
    private readonly ILogger<AzureReportService> _logger = logger;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IWebHostEnvironment _env = env;
    private readonly IConfiguration _config = config;
    private readonly AzureReportStore _repository = repository;
    private readonly ArmClient _arm = arm;
    private readonly TokenCredential _credential = credential;
    private readonly DowntimeDiagnosisService _downtimeDiagnosis = downtimeDiagnosis;
    private readonly PlanRecommendationService _planRecommendation = planRecommendation;

    /// <summary>Main orchestrator: discovers services, tests connectivity, fetches metrics, costs, and performs comprehensive Azure health analysis.</summary>
    public async Task<AzureReport> RunAsync(IProgress<(string Step, int Percent)>? progress = null, CancellationToken ct = default)
    {
        var stepTimings = new List<StepTimingEntry>();

        void Report(string step, int pct, string? detail = null)
        {
            _logger.LogInformation("[{Pct}%] {Step}{Detail}", pct, step, detail is not null ? $" — {detail}" : "");
            progress?.Report((step, pct));
        }

        async Task<T> RunTimedStepAsync<T>(string step, Func<Task<T>> action)
        {
            // Child span per step: every outbound ARM/GitHub dependency call the step makes
            // is parented under it, so "which step was slow / made these 400 calls" is
            // answerable in Azure Monitor instead of only via the hand-rolled StepTimings.
            using var activity = Telemetry.Source.StartActivity($"refresh.step {step}");
            var sw = Stopwatch.StartNew();
            var result = await action();
            sw.Stop();
            activity?.SetTag("refresh.step.elapsed_ms", sw.ElapsedMilliseconds);
            stepTimings.Add(new StepTimingEntry { Step = step, ElapsedMs = sw.ElapsedMilliseconds });
            _logger.LogInformation("Step '{Step}' completed in {ElapsedMs}ms", step, sw.ElapsedMilliseconds);
            return result;
        }

        var previousReportResult = await _repository.LoadPreviousAsync(ct);
        AzureReport? previousReport = null;
        if (previousReportResult.IsSuccess)
            previousReport = previousReportResult.Value;
        _logger.LogInformation("AzureReportService: starting analysis");

        Report("Authenticating with Azure…", 3);
        var cred = _credential;
        var arm = _arm;

        Report("Loading subscription…", 7);
        // Use configured subscription ID if set — avoids VS Code credential picking wrong account
        var configuredSubId = _config["Azure:SubscriptionId"];
        SubscriptionResource subscription;
        if (!string.IsNullOrWhiteSpace(configuredSubId))
        {
            var subResource = arm.GetSubscriptionResource(new ResourceIdentifier($"/subscriptions/{configuredSubId}"));
            subscription = (await subResource.GetAsync(ct)).Value;
        }
        else
        {
            subscription = await arm.GetDefaultSubscriptionAsync(ct);
        }
        var subscriptionId = subscription.Data.SubscriptionId!;
        _logger.LogInformation("Subscription: {Name} ({Id})", subscription.Data.DisplayName, subscriptionId);

        // Acquire ARM token early — shared across cost, plan resolution, and diagnosis steps
        string? armToken = null;
        try
        {
            armToken = (await cred.GetTokenAsync(
                new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not obtain ARM token — cost/plan/diagnosis will be unavailable"); }

        Report("Discovering web services…", 15, subscription.Data.DisplayName);
        var rawServices = await RunTimedStepAsync("Discovering web services", () => DiscoverWebServicesAsync(subscription, ct));
        _logger.LogInformation("Discovered {Count} web services", rawServices.Count);

        // Resolve App Service Plans (name + SKU) for each discovered site
        Report("Resolving App Service Plans…", 20);
        rawServices = await RunTimedStepAsync("Resolving App Service Plans", () => ResolveAppServicePlansAsync(rawServices, armToken, ct));

        Report("Testing connectivity…", 28, $"{rawServices.Count} services found");
        var connectedSvcs = await RunTimedStepAsync("Testing connectivity", () => TestConnectivityAsync(rawServices, ct));

        Report("Loading all resources…", 36);
        var allResources = await RunTimedStepAsync("Loading all resources", () => GetAllResourcesAsync(subscription, ct));
        _logger.LogInformation("Found {Count} total resources", allResources.Count);

        Report("Fetching metrics (7 days)…", 45, $"{allResources.Count} resources");
        var metricsMap = await RunTimedStepAsync("Fetching metrics (7 days)", () => GetMetricsAsync(connectedSvcs, cred, ct));

        // (ARM token already acquired above)

        Report("Fetching cost data…", 53);
        var costInfo = await RunTimedStepAsync("Fetching cost data", () => GetCostAsync(subscriptionId, armToken, ct));

        Report("Checking SSL certificates…", 60);
        var sslExpiry = await RunTimedStepAsync("Checking SSL certificates", () => CheckSslAsync(connectedSvcs, ct));

        Report("Checking configuration drift…", 65);
        var configDrift = await RunTimedStepAsync("Checking configuration drift", () => GetConfigDriftAsync(connectedSvcs, arm, ct));

        Report("Scanning storage accounts…", 70);
        var storageInv = await RunTimedStepAsync("Scanning storage accounts", () => GetStorageInventoryAsync(allResources, armToken, ct));

        Report("Scanning AI services…", 72);
        var aiServicesInv = await RunTimedStepAsync("Scanning AI services", () => GetAiServicesInventoryAsync(allResources, armToken, ct));

        Report("Scanning Log Analytics…", 73);
        var logAnalyticsInv = await RunTimedStepAsync("Scanning Log Analytics", () => GetLogAnalyticsInventoryAsync(allResources, armToken, ct));

        Report("Analysing free tiers & zombies…", 74);
        var freeTier = AnalyzeFreeTiers(allResources);
        var zombies = DetectZombies(connectedSvcs, metricsMap);

        Report("Diffing apps.json…", 77);
        var appsDiff = await RunTimedStepAsync("Diffing apps.json", () => DiffAppsJsonAsync(connectedSvcs, ct));

        Report("Calculating burn rate…", 80);
        var burnRate = await RunTimedStepAsync("Calculating burn rate", () => GetBurnRateAsync(subscriptionId, armToken, ct));

        Report("Scanning orphaned resources…", 83);
        var orphaned = await RunTimedStepAsync("Scanning orphaned resources", () => GetOrphanedResourcesAsync(allResources, armToken, ct));

        Report("Fetching App Insights metrics…", 86);
        var appInsights = await RunTimedStepAsync("Fetching App Insights metrics", () => GetAppInsightsMetricsAsync(allResources, cred, ct));

        var brokenAppServices = connectedSvcs
            .Where(s => s.HttpStatus != "active"
                && s.ResourceTypeRaw == "Microsoft.Web/sites"
                && s.ResourceId is not null)
            .ToList();

        // Build GitHub workflow run correlation map for broken services
        var gitHubRuns = new Dictionary<string, GitHubWorkflowRun>(StringComparer.OrdinalIgnoreCase);
        var infraReviews = await LoadInfraReviewsForCorrelationAsync(connectedSvcs, ct);
        foreach (var svc in brokenAppServices)
        {
            var matched = MatchServiceToInfraReview(svc, infraReviews);
            if (matched is not null)
            {
                gitHubRuns[svc.Name] = new GitHubWorkflowRun
                {
                    ServiceName = svc.Name,
                    RunUrl = matched.LatestWorkflowRunUrl,
                    Status = matched.LatestWorkflowRunStatus,
                    Conclusion = matched.LatestWorkflowRunConclusion,
                    CompletedAt = matched.LatestWorkflowRunCompletedAt,
                    RunName = matched.LatestWorkflowRunName,
                };
            }
        }

        Report("Diagnosing downtime causes…", 90, $"{brokenAppServices.Count} broken/unreachable services");
        var downtimeDiags = brokenAppServices.Count > 0
            ? await RunTimedStepAsync("Diagnosing downtime", () => _downtimeDiagnosis.DiagnoseAsync(brokenAppServices, subscriptionId, armToken, appInsights, gitHubRuns, ct))
            : new List<ServiceDowntimeDiagnosis>();

        var servicesList = connectedSvcs.Select(s =>
        {
            metricsMap.TryGetValue(s.ResourceId ?? "", out var m);
            return s with
            {
                Metrics7Days = s.ResourceId is not null ? m : null,
                FreeTierCheck = CheckFreeTierForService(s.ResourceTypeRaw, s.Sku),
            };
        }).ToList();

        var active = servicesList.Count(s => s.HttpStatus == "active");
        var broken = servicesList.Count(s => s.HttpStatus == "broken");
        var other = servicesList.Count(s => s.HttpStatus != "active" && s.HttpStatus != "broken");

        var webServices = servicesList.Select(s => s.ToWebService()).ToList();

        Report("Generating plan recommendations…", 93);
        var planRecommendations = _planRecommendation.Analyze(webServices, downtimeDiags, configDrift);
        _logger.LogInformation("Generated {Count} plan recommendations", planRecommendations.Count);

        var appServicePlanInventory = AppServicePlanInventory.BuildPoSharedPlanInventory(
            allResources.Select(r => new ResourceDetail
            {
                Name = r.Name,
                ResourceGroup = r.Id?.ResourceGroupName,
                Location = r.Location.Name,
                Sku = r.Sku?.Name?.ToString(),
                Type = r.ResourceType.ToString(),
            }),
            webServices);

        var report = new AzureReport
        {
            GeneratedAt = DateTime.UtcNow,
            Subscription = new SubscriptionInfo { Name = subscription.Data.DisplayName ?? subscriptionId },
            WebServices = new WebServicesInfo
            {
                Total = servicesList.Count,
                ByStatus = new ByStatusInfo { Active = active, Broken = broken, Other = other },
                Services = webServices,
            },
            Cost = costInfo,
            FreeTier = freeTier,
            AllResourceSummary = new AllResourceSummaryInfo
            {
                Total = allResources.Count,
                ByType = allResources
                    .GroupBy(r => ShortType(r.ResourceType.ToString()))
                    .ToDictionary(g => g.Key, g => g.Count()),
                ResourcesByType = allResources
                    .GroupBy(r => ShortType(r.ResourceType.ToString()))
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(r => new ResourceDetail
                        {
                            Name = r.Name,
                            ResourceGroup = r.Id?.ResourceGroupName,
                            Location = r.Location.Name,
                            Sku = r.Sku?.Name?.ToString(),
                            Type = r.ResourceType.ToString(),
                        }).OrderBy(x => x.Name).ToList()),
            },
            SslExpiry = sslExpiry,
            ConfigDrift = configDrift,
            StorageInventory = storageInv,
            AiServicesInventory = aiServicesInv,
            LogAnalyticsInventory = logAnalyticsInv,
            AppsJsonDiff = appsDiff,
            AppInsightsMetrics = appInsights,
            ZombieApps = zombies,
            OrphanedResources = orphaned,
            BurnRate = burnRate,
            StepTimings = stepTimings.OrderByDescending(x => x.ElapsedMs).ToList(),
            AppServicePlanInventory = appServicePlanInventory,
            DowntimeDiagnoses = downtimeDiags,
            PlanRecommendations = planRecommendations,
        };

        var delta = ComputeDelta(report, previousReport);
        report = report with { Delta = delta };

        _logger.LogInformation("AzureReportService: analysis complete");
        return report;
    }
}
