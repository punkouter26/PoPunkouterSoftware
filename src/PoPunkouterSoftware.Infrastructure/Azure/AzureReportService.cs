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
using PoPunkouterSoftware.Shared.Azure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;


namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Analyses an Azure subscription using the Azure SDK and DefaultAzureCredential.
/// Works locally (via az login / VS login) and on Azure (via Managed Identity).
/// Produces the same AzureReport structure consumed by AzureDashboard.razor.
/// </summary>
public class AzureReportService(
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

    #region Public Entry Point

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
            var sw = Stopwatch.StartNew();
            var result = await action();
            sw.Stop();
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

    #endregion

    #region Step 1: Discover Web Services

    private async Task<List<RawService>> DiscoverWebServicesAsync(SubscriptionResource sub, CancellationToken ct)
    {
        var list = new List<RawService>();

        // App Service web apps
        await foreach (var site in sub.GetWebSitesAsync(cancellationToken: ct))
        {
            if (site.Data.Name.Contains('/'))
                continue; // skip slots
            var url = site.Data.DefaultHostName is { } h ? $"https://{h}" : null;
            var rg = site.Data.Id?.ResourceGroupName ?? "";
            var isFunctionApp = site.Data.Kind?.Contains("functionapp", StringComparison.OrdinalIgnoreCase) == true;
            list.Add(new RawService
            {
                Name = site.Data.Name,
                FriendlyName = FriendlyFromContext(site.Data.Name, rg),
                ResourceGroup = rg,
                ResourceTypeRaw = isFunctionApp ? "Microsoft.Web/sites/functions" : "Microsoft.Web/sites",
                Kind = site.Data.Kind,
                Url = url,
                Sku = null, // SKU is on the App Service Plan, not the site
                PlatformState = site.Data.State,
                ResourceId = site.Data.Id?.ToString(),
            });
        }

        // Static Web Apps
        await foreach (var swa in sub.GetStaticSitesAsync(cancellationToken: ct))
        {
            var url = swa.Data.DefaultHostname is { } h ? $"https://{h}" : null;
            var rg = swa.Data.Id?.ResourceGroupName ?? "";
            list.Add(new RawService
            {
                Name = swa.Data.Name,
                FriendlyName = FriendlyFromContext(swa.Data.Name, rg),
                ResourceGroup = rg,
                ResourceTypeRaw = "Microsoft.Web/staticSites",
                Url = url,
                Sku = swa.Data.Sku?.Name ?? "Free",
                PlatformState = "Running",
                ResourceId = swa.Data.Id?.ToString(),
            });
        }

        // Container Apps (via generic ARM filter)
        await foreach (var ca in sub.GetGenericResourcesAsync(
            filter: "resourceType eq 'Microsoft.App/containerApps'",
            cancellationToken: ct))
        {
            var rg = ca.Data.Id?.ResourceGroupName ?? "";
            list.Add(new RawService
            {
                Name = ca.Data.Name,
                FriendlyName = FriendlyFromContext(ca.Data.Name, rg),
                ResourceGroup = rg,
                ResourceTypeRaw = "Microsoft.App/containerApps",
                Url = null,
                Sku = "Consumption",
                PlatformState = "Running",
                ResourceId = ca.Data.Id?.ToString(),
            });
        }

        return list;
    }

    #endregion

    #region Step 1b: Resolve App Service Plans (name + SKU)

    private async Task<List<RawService>> ResolveAppServicePlansAsync(
        List<RawService> services, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return services;

        var sites = services
            .Where(s => s.ResourceTypeRaw == "Microsoft.Web/sites" && s.ResourceId is not null)
            .ToList();

        if (sites.Count == 0)
            return services;

        var client = _httpClientFactory.CreateClient();
        var planSkus = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(6);
        var tasks = sites.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{svc.ResourceId}?api-version=2023-12-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                using var resp = await client.SendAsync(req, cts.Token);

                if (!resp.IsSuccessStatusCode)
                    return ((RawService?)null, (string?)null, (string?)null);

                var json = await resp.Content.ReadAsStringAsync(cts.Token);
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var props))
                    return ((RawService?)null, (string?)null, (string?)null);

                var serverFarmId = props.TryGetProperty("serverFarmId", out var sfId) ? sfId.GetString() : null;
                if (serverFarmId is null)
                    return ((RawService?)null, (string?)null, (string?)null);

                var planName = serverFarmId.Split('/').LastOrDefault();
                if (planName is null)
                    return ((RawService?)null, (string?)null, (string?)null);

                string? planSku = null;
                if (!planSkus.TryGetValue(serverFarmId, out planSku))
                {
                    try
                    {
                        using var planReq = new HttpRequestMessage(HttpMethod.Get,
                            $"https://management.azure.com{serverFarmId}?api-version=2023-12-01");
                        planReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                        using var planResp = await client.SendAsync(planReq, cts.Token);

                        if (planResp.IsSuccessStatusCode)
                        {
                            var planJson = await planResp.Content.ReadAsStringAsync(cts.Token);
                            var planDoc = JsonDocument.Parse(planJson);
                            if (planDoc.RootElement.TryGetProperty("sku", out var sku))
                                planSku = sku.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                        }
                    }
                    catch { /* plan fetch failed — leave SKU null */ }

                    planSkus[serverFarmId] = planSku ?? "";
                }

                return (svc, planName, planSku);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Plan resolution failed for {Name}", svc.Name);
                return ((RawService?)null, (string?)null, (string?)null);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var (svc, planName, planSku) in results)
        {
            if (svc is null || planName is null)
                continue;

            var idx = services.FindIndex(s => s.ResourceId == svc.ResourceId);
            if (idx >= 0)
                services[idx] = services[idx] with { AppServicePlan = planName, AppServicePlanSku = planSku };
        }

        return services;
    }

    #endregion

    #region Step 2: Test HTTP Connectivity

    private async Task<List<RawService>> TestConnectivityAsync(List<RawService> services, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("azure-probe");
        var tasks = services.Select(async svc =>
        {
            if (string.IsNullOrEmpty(svc.Url))
                return svc with { Connectivity = new ConnectivityInfo { Success = false, Error = "No URL" }, HttpStatus = "unknown" };
            var conn = await ProbeUrlAsync(client, svc.Url, ct);
            var status = conn.Success ? "active" : conn.ResponseTime > 0 ? "broken" : "unreachable";
            return svc with { Connectivity = conn, HttpStatus = status };
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<ConnectivityInfo> ProbeUrlAsync(HttpClient client, string url, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            var isAzureError = resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway;
            return new ConnectivityInfo
            {
                Success = resp.IsSuccessStatusCode && !isAzureError,
                ResponseTime = (int)sw.ElapsedMilliseconds,
                Error = isAzureError ? "Azure error page" : null,
                IsAzureErrorPage = isAzureError,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectivityInfo { Success = false, ResponseTime = (int)sw.ElapsedMilliseconds, Error = ex.Message };
        }
    }

    #endregion

    #region Step 3: Load All ARM Resources

    private static async Task<List<GenericResourceData>> GetAllResourcesAsync(SubscriptionResource sub, CancellationToken ct)
    {
        var list = new List<GenericResourceData>();
        await foreach (var r in sub.GetGenericResourcesAsync(cancellationToken: ct))
            list.Add(r.Data);
        return list;
    }

    #endregion

    #region Step 4: Fetch Metrics (7 Days)

    private async Task<Dictionary<string, MetricsInfo>> GetMetricsAsync(
        List<RawService> services, TokenCredential cred, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, MetricsInfo>(StringComparer.OrdinalIgnoreCase);
        var appSvcs = services.Where(s => s.ResourceId is not null && s.ResourceTypeRaw == "Microsoft.Web/sites").ToList();
        if (appSvcs.Count == 0)
            return [];

        MetricsQueryClient metricsClient;
        try
        { metricsClient = new MetricsQueryClient(cred); }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "MetricsQueryClient could not be initialised — skipping metrics (expected in local dev without App Insights)");
            return [];
        }

        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-7);

        using var gate = new SemaphoreSlim(6);
        var tasks = appSvcs.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var response = await metricsClient.QueryResourceAsync(
                    svc.ResourceId!,
                    ["Requests", "Http5Xx", "AverageResponseTime"],
                    new MetricsQueryOptions { TimeRange = new QueryTimeRange(start, end), Granularity = TimeSpan.FromDays(1) },
                    ct);

                int requests = 0, http5xx = 0;
                double avgRt = 0;
                foreach (var metric in response.Value.Metrics)
                {
                    var total = metric.TimeSeries.SelectMany(ts => ts.Values)
                        .Sum(p => p.Total ?? p.Average ?? 0);
                    if (metric.Name.Contains("Request", StringComparison.OrdinalIgnoreCase) && !metric.Name.Contains("5"))
                        requests = (int)total;
                    else if (metric.Name.Contains("5", StringComparison.OrdinalIgnoreCase))
                        http5xx = (int)total;
                    else if (metric.Name.Contains("Response", StringComparison.OrdinalIgnoreCase))
                        avgRt = Math.Round(total, 1);
                }

                result[svc.ResourceId!] = new MetricsInfo
                {
                    Requests = requests,
                    Http5xx = http5xx,
                    AverageResponseTime = avgRt,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Metrics unavailable for {Name}", svc.Name);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ── Step 5: 30-day cost via Cost Management REST ───────────────────────────
        #endregion

        #region Step 5: Fetch Cost Data (30 Days)

        /// <summary>Fetches 30-day usage and cost from the Cost Management API; calculates monthly burn rate.</summary>

    private async Task<CostInfo> GetCostAsync(string subscriptionId, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return new CostInfo { Note = "Cost data unavailable (no ARM token)" };
        try
        {
            var today = DateTime.UtcNow.Date;
            var start = today.AddDays(-30);
            var body = JsonSerializer.Serialize(new
            {
                type = "Usage",
                timeframe = "Custom",
                timePeriod = new { from = start.ToString("yyyy-MM-dd"), to = today.ToString("yyyy-MM-dd") },
                dataset = new
                {
                    granularity = "None",
                    aggregation = new { totalCost = new { name = "PreTaxCost", function = "Sum" } },
                    grouping = new[]
                    {
                        new { type = "Dimension", name = "ServiceName" },
                        new { type = "Dimension", name = "ResourceGroupName" },
                    },
                },
            });

            var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
            var json = await PostCostManagementWithRetryAsync(url, body, armToken, ct);
            if (json is null)
                return new CostInfo { Note = "Cost data unavailable (rate-limited or request failed)" };

            var doc = JsonDocument.Parse(json);
            var props = doc.RootElement.GetProperty("properties");
            var rows = props.GetProperty("rows").EnumerateArray().ToList();
            var cols = props.GetProperty("columns").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()!.ToLowerInvariant()).ToList();

            int costIdx = cols.FindIndex(c => c.Contains("pretax") || c.Contains("cost"));
            int svcIdx = cols.FindIndex(c => c.Contains("service"));
            int rgIdx = cols.FindIndex(c => c.Contains("resourcegroup"));

            double totalCost = 0;
            var byKey = new Dictionary<string, double>();
            foreach (var row in rows)
            {
                var arr = row.EnumerateArray().ToArray();
                var cost = costIdx >= 0 ? arr[costIdx].GetDouble() : 0;
                var svc = svcIdx >= 0 ? arr[svcIdx].GetString() ?? "Unknown" : "Unknown";
                var rg = rgIdx >= 0 ? arr[rgIdx].GetString() ?? "" : "";
                var key = string.IsNullOrEmpty(rg) ? svc : $"{svc} ({rg})";
                byKey[key] = byKey.GetValueOrDefault(key) + cost;
                totalCost += cost;
            }

            var drivers = byKey
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value)
                .Take(20)
                .Select(kv => new CostDriver { Name = kv.Key, Cost = Math.Round(kv.Value, 4) })
                .ToList();

            return new CostInfo
            {
                TotalCost30Days = Math.Round(totalCost, 4),
                TotalFormatted = $"${totalCost:F2}",
                TopCostDrivers = drivers,
                Note = totalCost == 0 ? "All costs $0.00 — subscription may be covered by credits." : null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cost analysis failed");
            return new CostInfo { Note = $"Cost data unavailable: {ex.Message}" };
        }
    }

    #endregion

    #region Step 6: Check SSL Certificate Expiry

    private async Task<List<SslEntry>> CheckSslAsync(List<RawService> services, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(8);
        var tasks = services.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (svc.Url is not { } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return new SslEntry { Name = svc.Name, Url = svc.Url, Error = "Non-HTTPS" };

                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return new SslEntry { Name = svc.Name, Url = url, Error = "Invalid URL" };

                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(uri.Host, 443, cts.Token);
                    using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.Host,
                    }, cts.Token);

                    var cert = ssl.RemoteCertificate;
                    if (cert is null)
                        return new SslEntry { Name = svc.Name, Url = url, Error = "No cert" };

                    var expiry = DateTime.Parse(cert.GetExpirationDateString());
                    var daysLeft = (int)(expiry - DateTime.UtcNow).TotalDays;
                    return new SslEntry { Name = svc.Name, Url = url, Expiry = expiry.ToString("yyyy-MM-dd"), DaysLeft = daysLeft, Subject = cert.Subject };
                }
                catch (Exception ex)
                {
                    return new SslEntry { Name = svc.Name, Url = url, Error = ex.Message };
                }
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    #endregion

    #region Step 7: Detect Configuration Drift

    private async Task<List<ConfigDriftItem>> GetConfigDriftAsync(
        List<RawService> services, ArmClient arm, CancellationToken ct)
    {
        var targets = services.Where(s => s.ResourceTypeRaw == "Microsoft.Web/sites" && s.ResourceId is not null).ToList();
        using var gate = new SemaphoreSlim(6);
        var tasks = targets.Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Get the site config child resource directly by resource ID (no RG traversal needed)
                var siteRes = arm.GetWebSiteResource(new ResourceIdentifier(svc.ResourceId!));
                var configRes = siteRes.GetWebSiteConfig();
                var configResp = await configRes.GetAsync(cancellationToken: ct);
                var cfg = configResp.Value.Data;

                var issues = new List<ConfigIssue>();
                if (cfg.FtpsState is not null &&
                    cfg.FtpsState != AppServiceFtpsState.Disabled &&
                    cfg.FtpsState != AppServiceFtpsState.FtpsOnly)
                    issues.Add(new ConfigIssue { Severity = "high", Issue = $"FTP enabled ({cfg.FtpsState}) — use FTPS-only or Disabled" });
                if (cfg.IsHttp20Enabled == false)
                    issues.Add(new ConfigIssue { Severity = "low", Issue = "HTTP/2 disabled" });
                if (cfg.MinTlsVersion is not null &&
                    string.Compare(cfg.MinTlsVersion.ToString(), "1.2", StringComparison.Ordinal) < 0)
                    issues.Add(new ConfigIssue { Severity = "high", Issue = $"Min TLS {cfg.MinTlsVersion} — must be ≥1.2" });
                if (cfg.IsAlwaysOn == false)
                    issues.Add(new ConfigIssue { Severity = "low", Issue = "Always-On disabled (cold starts)" });
                if (cfg.Cors?.AllowedOrigins?.Contains("*") == true)
                    issues.Add(new ConfigIssue { Severity = "medium", Issue = "CORS * — all origins allowed" });

                return new ConfigDriftItem
                {
                    Name = svc.Name,
                    FriendlyName = svc.FriendlyName,
                    ResourceGroup = svc.ResourceGroup,
                    IssueCount = issues.Count,
                    Issues = issues,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Config drift check failed for {Name}", svc.Name);
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.OfType<ConfigDriftItem>().OrderBy(x => x.Name).ToList();
    }

    #endregion

    #region Step 8: Inventory Storage Accounts

    private async Task<List<StorageItem>> GetStorageInventoryAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var results = new List<StorageItem>();
        if (armToken is null)
            return results;
        var storages = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "Microsoft.Storage/storageAccounts", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (storages.Count == 0)
            return results;

        var client = _httpClientFactory.CreateClient();

        async Task<StorageItem?> CheckOneAsync(GenericResourceData sa)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{sa.Id}?api-version=2023-01-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, cts.Token);

                bool publicBlob = false;
                bool httpsOnly = true;
                string? minTls = null;

                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cts.Token);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("properties", out var p))
                    {
                        if (p.TryGetProperty("allowBlobPublicAccess", out var pub))
                            publicBlob = pub.GetBoolean();
                        if (p.TryGetProperty("supportsHttpsTrafficOnly", out var https))
                            httpsOnly = https.GetBoolean();
                        if (p.TryGetProperty("minimumTlsVersion", out var tls))
                            minTls = tls.GetString();
                    }
                }

                var issues = new List<StorageIssue>();
                if (publicBlob)
                    issues.Add(new StorageIssue { Severity = "high", Issue = "Public blob access enabled — potential data exposure" });
                if (!httpsOnly)
                    issues.Add(new StorageIssue { Severity = "high", Issue = "HTTPS-only is off — HTTP traffic allowed" });
                if (minTls is not null && string.Compare(minTls, "TLS1_2", StringComparison.Ordinal) < 0)
                    issues.Add(new StorageIssue { Severity = "medium", Issue = $"Min TLS {minTls} — upgrade to TLS 1.2" });

                return new StorageItem
                {
                    Name = sa.Name,
                    ResourceGroup = sa.Id?.ResourceGroupName,
                    Sku = sa.Sku?.Name?.ToString(),
                    PublicBlobAccess = publicBlob,
                    HttpsOnly = httpsOnly,
                    MinTls = minTls,
                    IssueCount = issues.Count,
                    Issues = issues,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Storage check failed for {Name}", sa.Name);
                return null;
            }
        }

        var items = await Task.WhenAll(storages.Select(CheckOneAsync));
        results.AddRange(items.OfType<StorageItem>());
        return results;
    }

    #endregion

    #region Step 8b: Inventory AI Services

    private async Task<List<AiServiceInventoryItem>> GetAiServicesInventoryAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var accounts = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "Microsoft.CognitiveServices/accounts", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (accounts.Count == 0)
            return [];

        var client = _httpClientFactory.CreateClient();

        async Task<AiServiceInventoryItem> InspectAsync(GenericResourceData account)
        {
            string? endpoint = null;
            var deployments = new List<string>();

            if (armToken is not null)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{account.Id}?api-version=2023-05-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("properties", out var props)
                            && props.TryGetProperty("endpoint", out var ep))
                            endpoint = ep.GetString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AI service detail fetch failed for {Name}", account.Name);
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{account.Id}/deployments?api-version=2023-05-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("value", out var value)
                            && value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var deployment in value.EnumerateArray())
                            {
                                var name = deployment.TryGetProperty("name", out var nameEl)
                                    ? nameEl.GetString()
                                    : null;
                                var model = deployment.TryGetProperty("properties", out var props)
                                    && props.TryGetProperty("model", out var modelEl)
                                    && modelEl.TryGetProperty("name", out var modelName)
                                        ? modelName.GetString()
                                        : null;
                                deployments.Add(string.IsNullOrWhiteSpace(model) ? name ?? "deployment" : $"{name} ({model})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AI deployment fetch failed for {Name}", account.Name);
                }
            }

            var sku = account.Sku?.Name?.ToString();
            var kind = account.Kind;
            var risk = "watch";
            var recommendation = "Track usage and keep budget alerts enabled.";

            if (string.Equals(sku, "S0", StringComparison.OrdinalIgnoreCase) && deployments.Count == 0)
            {
                risk = "cleanup";
                recommendation = "S0 account has no deployments. Confirm it is unused, then delete or downgrade.";
            }
            else if (string.Equals(sku, "S0", StringComparison.OrdinalIgnoreCase))
            {
                risk = "cost";
                recommendation = "Paid AI account. Review token/call volume and cap spend with Azure budgets.";
            }
            else if (string.Equals(sku, "F0", StringComparison.OrdinalIgnoreCase))
            {
                risk = "ok";
                recommendation = "Free-tier account. Keep unless it duplicates another AI endpoint.";
            }

            return new AiServiceInventoryItem
            {
                Name = account.Name,
                ResourceGroup = account.Id?.ResourceGroupName,
                Location = account.Location.Name,
                Kind = kind,
                Sku = sku,
                Endpoint = endpoint,
                DeploymentCount = deployments.Count,
                Deployments = deployments.OrderBy(x => x).ToList(),
                Recommendation = recommendation,
                RiskLevel = risk,
            };
        }

        var results = await Task.WhenAll(accounts.Select(InspectAsync));
        return results.OrderBy(x => x.RiskLevel).ThenBy(x => x.Name).ToList();
    }

    #endregion

    #region Step 8c: Inventory Log Analytics

    private async Task<List<LogAnalyticsWorkspaceItem>> GetLogAnalyticsInventoryAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var workspaces = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "Microsoft.OperationalInsights/workspaces", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (workspaces.Count == 0)
            return [];

        var client = _httpClientFactory.CreateClient();

        async Task<LogAnalyticsWorkspaceItem> InspectAsync(GenericResourceData workspace)
        {
            string? sku = workspace.Sku?.Name?.ToString();
            int? retention = null;
            double? dailyQuota = null;

            if (armToken is not null)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get,
                        $"https://management.azure.com{workspace.Id}?api-version=2022-10-01");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
                    using var resp = await client.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("properties", out var props))
                        {
                            if (props.TryGetProperty("retentionInDays", out var ret) && ret.ValueKind == JsonValueKind.Number)
                                retention = ret.GetInt32();
                            if (props.TryGetProperty("workspaceCapping", out var cap)
                                && cap.TryGetProperty("dailyQuotaGb", out var quota)
                                && quota.ValueKind == JsonValueKind.Number)
                                dailyQuota = quota.GetDouble();
                        }
                        if (doc.RootElement.TryGetProperty("sku", out var skuEl)
                            && skuEl.TryGetProperty("name", out var skuName))
                            sku = skuName.GetString();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Log Analytics detail fetch failed for {Name}", workspace.Name);
                }
            }

            var risk = "ok";
            var recommendation = "Retention and cap look reasonable.";
            if (dailyQuota is null or <= 0)
            {
                risk = "cost";
                recommendation = "No daily ingestion cap is visible. Set a cap such as 0.5-1 GB/day for hobby apps.";
            }
            else if (retention is > 30)
            {
                risk = "cost";
                recommendation = "Retention is above 30 days. Reduce to 7-14 days if long history is not needed.";
            }
            else if (retention is null)
            {
                risk = "watch";
                recommendation = "Retention was not reported. Verify retention and daily cap in Azure.";
            }

            return new LogAnalyticsWorkspaceItem
            {
                Name = workspace.Name,
                ResourceGroup = workspace.Id?.ResourceGroupName,
                Location = workspace.Location.Name,
                Sku = sku,
                RetentionInDays = retention,
                DailyQuotaGb = dailyQuota,
                Recommendation = recommendation,
                RiskLevel = risk,
            };
        }

        var results = await Task.WhenAll(workspaces.Select(InspectAsync));
        return results.OrderByDescending(x => x.RiskLevel == "cost").ThenBy(x => x.Name).ToList();
    }

    #endregion

    #region Step 9: Analyze Free-Tier Usage

    private static FreeTierInfo AnalyzeFreeTiers(List<GenericResourceData> resources)
    {
        var onFree = new List<FreeTierItem>();
        var canGoFree = new List<FreeTierItem>();

        foreach (var r in resources)
        {
            var typeKey = r.ResourceType.ToString();
            if (!FreeTierMap.TryGetValue(typeKey, out var info))
                continue;

            var currentSku = r.Sku?.Name?.ToString() ?? r.Kind ?? "unknown";
            var isOnFree = info.FreeSku is not null &&
                              string.Equals(currentSku, info.FreeSku, StringComparison.OrdinalIgnoreCase);
            var canGoToFree = info.FreeSku is not null && !isOnFree;

            var entry = new FreeTierItem
            {
                Name = r.Name,
                Label = info.Label,
                CurrentSku = currentSku,
                FreeSku = info.FreeSku,
                FreeSkuLabel = info.FreeSkuLabel,
                ResourceGroup = r.Id?.ResourceGroupName,
                Recommendation = info.Note,
            };

            if (isOnFree)
                onFree.Add(entry);
            else if (canGoToFree)
                canGoFree.Add(entry);
        }

        return new FreeTierInfo { OnFree = onFree, CanGoFree = canGoFree };
    }

    #endregion

    #region Step 10: Detect Zombie Applications

    private static FreeTierCheckInfo? CheckFreeTierForService(string typeKey, string? sku)
    {
        if (!FreeTierMap.TryGetValue(typeKey, out var info))
            return null;
        var isOnFree = info.FreeSku is not null &&
                       string.Equals(sku, info.FreeSku, StringComparison.OrdinalIgnoreCase);
        return new FreeTierCheckInfo
        {
            IsOnFreeTier = isOnFree,
            IsOnPaidTier = !isOnFree && info.PaidSkus.Any(p => string.Equals(sku, p, StringComparison.OrdinalIgnoreCase)),
            CanGoFree = info.FreeSku is not null && !isOnFree,
        };
    }

    private static List<ZombieApp> DetectZombies(List<RawService> services, Dictionary<string, MetricsInfo> metricsMap)
        => services
            .Where(s => s.ResourceTypeRaw == "Microsoft.Web/sites" && s.ResourceId is not null)
            // Exclude WebSocket-only, SignalR, and background worker services:
            // - SignalR hubs (kind: "signalr") don't serve HTTP pages
            // - WebJob/background services are not front-end web apps
            // - Kind containing "functionapp" are Azure Functions, not web apps
            .Where(s => !string.IsNullOrEmpty(s.Kind) &&
                        !s.Kind.Contains("signalr", StringComparison.OrdinalIgnoreCase) &&
                        !s.Kind.Contains("functionapp", StringComparison.OrdinalIgnoreCase) &&
                        !s.Kind.Contains("workflowapp", StringComparison.OrdinalIgnoreCase) &&
                        s.PlatformState != "Stopped")
            .Where(s => metricsMap.TryGetValue(s.ResourceId!, out var m) && m.Requests == 0)
            .Select(s => new ZombieApp
            {
                Name = s.Name,
                ResourceGroup = s.ResourceGroup,
                HttpStatus = s.HttpStatus,
                PlatformState = s.PlatformState,
                Recommendation = $"az webapp stop --name \"{s.Name}\" --resource-group \"{s.ResourceGroup}\"",
            })
            .ToList();

    #endregion

    #region Step 11: Compare Against Apps.json Snapshot

    /// <summary>Diffs current discovered services against the snapshot stored in apps.json.</summary>
    private async Task<AppsJsonDiffInfo?> DiffAppsJsonAsync(List<RawService> services, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(_env.WebRootPath, "data", "apps.json");
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, ct);
            var doc = JsonDocument.Parse(json);
            var existing = doc.RootElement.TryGetProperty("apps", out var appsEl)
                ? appsEl.EnumerateArray()
                    .Select(a => a.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .Where(id => id is not null)
                    .ToHashSet()!
                : new HashSet<string?>();

            var discovered = services.Select(s => GetCanonicalName(s.Name)).ToHashSet();
            return new AppsJsonDiffInfo
            {
                CurrentCount = existing.Count,
                DiscoveredCount = discovered.Count,
                NewApps = discovered.Except(existing).ToList()!,
                RemovedApps = existing.Except(discovered).ToList()!,
                UpdatedApps = discovered.Intersect(existing).ToList()!,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "apps.json diff failed");
            return null;
        }
    }

    #endregion

    #region Step 12: Identify Orphaned Resources

    private async Task<List<OrphanedResource>> GetOrphanedResourcesAsync(
        List<GenericResourceData> allResources, string? armToken, CancellationToken ct)
    {
        var orphans = new List<OrphanedResource>();
        if (armToken is null)
            return orphans;
        var client = _httpClientFactory.CreateClient();

        // 1 — Unattached managed disks
        foreach (var disk in allResources.Where(r =>
            r.ResourceType.ToString().Equals("Microsoft.Compute/disks", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{disk.Id}?api-version=2023-10-02");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var props))
                    continue;
                if (!props.TryGetProperty("diskState", out var state) || state.GetString() != "Unattached")
                    continue;

                var sizeGb = props.TryGetProperty("diskSizeGB", out var sz) ? sz.GetInt32() : 0;
                var sku = doc.RootElement.TryGetProperty("sku", out var skuEl) &&
                             skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                orphans.Add(new OrphanedResource
                {
                    Name = disk.Name,
                    ResourceGroup = disk.Id?.ResourceGroupName,
                    Type = "Managed Disk",
                    Reason = $"Unattached ({sizeGb} GB, {sku ?? "unknown SKU"})",
                    EstimatedMonthlyCost = sizeGb > 0 ? $"~${sizeGb * 0.04:F2}/mo" : null,
                    Command = $"az disk delete --name \"{disk.Name}\" --resource-group \"{disk.Id?.ResourceGroupName}\" --yes",
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Disk orphan check failed for {Name}", disk.Name); }
        }

        // 2 — Unattached public IPs
        foreach (var ip in allResources.Where(r =>
            r.ResourceType.ToString().Equals("Microsoft.Network/publicIPAddresses", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{ip.Id}?api-version=2023-11-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("properties", out var props))
                    continue;

                var hasIpConfig = props.TryGetProperty("ipConfiguration", out _);
                var hasNatGateway = props.TryGetProperty("natGateway", out _);
                if (hasIpConfig || hasNatGateway)
                    continue;

                var sku = doc.RootElement.TryGetProperty("sku", out var skuEl) &&
                          skuEl.TryGetProperty("name", out var skuName) ? skuName.GetString() : null;
                orphans.Add(new OrphanedResource
                {
                    Name = ip.Name,
                    ResourceGroup = ip.Id?.ResourceGroupName,
                    Type = "Public IP",
                    Reason = $"Not associated with any NIC or NAT gateway (SKU: {sku ?? "—"})",
                    EstimatedMonthlyCost = sku == "Standard" ? "~$3.65/mo" : null,
                    Command = $"az network public-ip delete --name \"{ip.Name}\" --resource-group \"{ip.Id?.ResourceGroupName}\"",
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Public IP orphan check failed for {Name}", ip.Name); }
        }

        // 3 — Empty App Service Plans
        foreach (var farm in allResources.Where(r =>
            r.ResourceType.ToString().Equals("Microsoft.Web/serverFarms", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://management.azure.com{farm.Id}/sites?api-version=2023-12-01");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken!);
                using var resp = await client.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);
                var siteCount = doc.RootElement.TryGetProperty("value", out var v) ? v.GetArrayLength() : 1;
                if (siteCount > 0)
                    continue;

                var sku = farm.Sku?.Name?.ToString() ?? "unknown";
                orphans.Add(new OrphanedResource
                {
                    Name = farm.Name,
                    ResourceGroup = farm.Id?.ResourceGroupName,
                    Type = "App Service Plan",
                    Reason = $"No apps deployed (SKU: {sku})",
                    EstimatedMonthlyCost = sku is "F1" or "FREE" ? "$0/mo (Free)" : "Paid tier — check portal",
                    Command = $"az appservice plan delete --name \"{farm.Name}\" --resource-group \"{farm.Id?.ResourceGroupName}\" --yes",
                });
            }
            catch (Exception ex) { _logger.LogDebug(ex, "App Service Plan orphan check failed for {Name}", farm.Name); }
        }

        return orphans;
    }

    #endregion

    #region Step 13: Compute Monthly Burn Rate

    private async Task<BurnRateInfo?> GetBurnRateAsync(
        string subscriptionId, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return null;
        try
        {
            var today = DateTime.UtcNow.Date;
            var startOfMonth = new DateTime(today.Year, today.Month, 1);
            if (startOfMonth == today)
                startOfMonth = today.AddDays(-1);

            var body = JsonSerializer.Serialize(new
            {
                type = "Usage",
                timeframe = "Custom",
                timePeriod = new { from = startOfMonth.ToString("yyyy-MM-dd"), to = today.ToString("yyyy-MM-dd") },
                dataset = new
                {
                    granularity = "Daily",
                    aggregation = new { totalCost = new { name = "PreTaxCost", function = "Sum" } },
                },
            });

            var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
            var json = await PostCostManagementWithRetryAsync(url, body, armToken, ct);
            if (json is null)
                return null;

            var doc = JsonDocument.Parse(json);
            var props = doc.RootElement.GetProperty("properties");
            var rows = props.GetProperty("rows").EnumerateArray().ToList();
            var cols = props.GetProperty("columns").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString()!.ToLowerInvariant()).ToList();

            int costIdx = cols.FindIndex(c => c.Contains("pretax") || c.Contains("cost"));
            int dateIdx = cols.FindIndex(c => c.Contains("date") || c.Contains("usage"));

            var daily = new List<DailyCostEntry>();
            foreach (var row in rows)
            {
                var arr = row.EnumerateArray().ToArray();
                var cost = costIdx >= 0 ? arr[costIdx].GetDouble() : 0;
                var raw = dateIdx >= 0
                    ? arr[dateIdx].ValueKind == JsonValueKind.Number
                        ? arr[dateIdx].GetInt32().ToString()
                        : arr[dateIdx].GetString() ?? ""
                    : "";
                var dateStr = raw.Length == 8 && raw.All(char.IsDigit)
                    ? $"{raw[..4]}-{raw[4..6]}-{raw[6..8]}"
                    : raw;
                daily.Add(new DailyCostEntry { Date = dateStr, Cost = Math.Round(cost, 4) });
            }

            daily = daily.OrderBy(d => d.Date).ToList();
            var totalSoFar = daily.Sum(d => d.Cost);
            var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
            var daysElapsed = Math.Max(1, (today - startOfMonth).Days + 1);
            var projected = Math.Round(totalSoFar / daysElapsed * daysInMonth, 2);

            return new BurnRateInfo
            {
                DailyCosts = daily,
                ProjectedMonthTotal = projected,
                ProjectedFormatted = $"${projected:F2}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Burn rate query failed");
            return null;
        }
    }

    #endregion

    #region Step 14: Fetch Application Insights Metrics

    /// <summary>Retrieves request rates, exception rates, and performance metrics from Application Insights.</summary>

    private async Task<string?> PostCostManagementWithRetryAsync(
        string url, string body, string? armToken, CancellationToken ct)
    {
        if (armToken is null)
            return null;
        var client = _httpClientFactory.CreateClient();
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken);
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < maxRetries - 1)
            {
                int delaySec = 30;
                if (resp.Headers.RetryAfter?.Delta.HasValue == true)
                    delaySec = (int)resp.Headers.RetryAfter.Delta!.Value.TotalSeconds;
                else if (resp.Headers.RetryAfter?.Date.HasValue == true)
                    delaySec = (int)(resp.Headers.RetryAfter.Date!.Value - DateTimeOffset.UtcNow).TotalSeconds;

                delaySec = Math.Clamp(delaySec, 1, 65);
                _logger.LogWarning("Cost Management returned TooManyRequests; retrying in {Delay}s (attempt {Attempt}/{Max})",
                    delaySec, attempt + 1, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                continue;
            }

            _logger.LogWarning("Cost Management returned {Status}", resp.StatusCode);
            return null;
        }
        return null;
    }

    // ── New: App Insights component metrics ───────────────────────────────────

    private async Task<List<AppInsightsMetric>> GetAppInsightsMetricsAsync(
        List<GenericResourceData> allResources, TokenCredential cred, CancellationToken ct)
    {
        var components = allResources
            .Where(r => r.ResourceType.ToString().Equals(
                "microsoft.insights/components", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (components.Count == 0)
            return [];

        // App Insights telemetry (requests, exceptions) lives in Log Analytics —
        // it cannot be queried via MetricsQueryClient. Use LogsQueryClient instead.
        LogsQueryClient logsClient;
        try
        { logsClient = new LogsQueryClient(cred); }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "LogsQueryClient unavailable for App Insights — expected in local dev without connection string");
            return [];
        }

        var timeRange = new QueryTimeRange(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow);

        using var gate = new SemaphoreSlim(4);
        var tasks = components.Select(async comp =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var resourceId = new ResourceIdentifier(comp.Id!.ToString());

                int requests = 0, failed = 0, exceptions = 0;

                // Requests + failures
                var reqResp = await logsClient.QueryResourceAsync(
                    resourceId,
                    "requests | summarize totalCount=count(), failedCount=countif(success==false)",
                    timeRange,
                    cancellationToken: ct);
                if (reqResp.Value?.Table?.Rows is { Count: > 0 } reqRows)
                {
                    requests = (int)(reqRows[0].GetInt64(0) ?? 0L);
                    failed = (int)(reqRows[0].GetInt64(1) ?? 0L);
                }

                // Exceptions
                var excResp = await logsClient.QueryResourceAsync(
                    resourceId,
                    "exceptions | summarize exCount=count()",
                    timeRange,
                    cancellationToken: ct);
                if (excResp.Value?.Table?.Rows is { Count: > 0 } excRows)
                    exceptions = (int)(excRows[0].GetInt64(0) ?? 0L);

                return new AppInsightsMetric
                {
                    Name = comp.Name,
                    ResourceGroup = comp.Id?.ResourceGroupName,
                    Requests7Days = requests,
                    FailedRequests7Days = failed,
                    Exceptions7Days = exceptions,
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "App Insights logs query failed for {Name}", comp.Name);
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.OfType<AppInsightsMetric>().OrderBy(x => x.Name).ToList();
    }

    #endregion

    #region Supporting Helpers: Delta Computation

    private static ReportDelta? ComputeDelta(AzureReport current, AzureReport? previous)
    {
        if (previous is null)
            return null;

        var currentBroken = current.WebServices?.Services
            .Where(s => s.HttpStatus == "broken").Select(s => s.Name).ToHashSet() ?? [];
        var previousBroken = previous.WebServices?.Services
            .Where(s => s.HttpStatus == "broken").Select(s => s.Name).ToHashSet() ?? [];

        var currentOrphaned = current.OrphanedResources?.Select(o => o.Name).ToHashSet() ?? [];
        var previousOrphaned = previous.OrphanedResources?.Select(o => o.Name).ToHashSet() ?? [];

        var costDelta = current.Cost is not null && previous.Cost is not null
            ? Math.Round(current.Cost.TotalCost30Days - previous.Cost.TotalCost30Days, 4)
            : (double?)null;

        return new ReportDelta
        {
            PreviousGeneratedAt = previous.GeneratedAt,
            BrokenServicesDelta = currentBroken.Count - previousBroken.Count,
            CostDelta = costDelta,
            NewBrokenServices = currentBroken.Except(previousBroken).ToList(),
            RecoveredServices = previousBroken.Except(currentBroken).ToList(),
            NewOrphanedResources = currentOrphaned.Except(previousOrphaned).ToList(),
        };
    }

    #endregion

    #region Supporting Helpers: Name Formatting

    private static string GetCanonicalName(string name)
    {
        var r = System.Text.RegularExpressions.Regex.Replace(
            name, @"^(swa-|stapp-|wa-|app-|api-|ca-)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        r = System.Text.RegularExpressions.Regex.Replace(
            r, @"(-api|-web|-server|-app|-prod)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        r = System.Text.RegularExpressions.Regex.Replace(
            r, @"-[a-z0-9]{9,}$",
            m => m.Value.TrimStart('-') is { } seg && seg.Any(char.IsDigit) && seg.Any(char.IsLetter) ? "" : m.Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return r.ToLowerInvariant();
    }

    private static string FriendlyFromContext(string rawName, string? resourceGroup)
    {
        if (resourceGroup is { Length: > 2 } rg && char.IsUpper(rg[2]) && rg != "PoShared")
            return rg;
        var canonical = GetCanonicalName(rawName);
        var parts = canonical.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var deduped = parts.Where((p, i) => i == 0 || p != parts[i - 1]).ToArray();
        var clean = System.Text.RegularExpressions.Regex.Replace(
            string.Join("-", deduped), "^po", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (string.IsNullOrEmpty(clean))
            return rawName;
        return "Po" + string.Concat(clean.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpper(p[0]) + p[1..]));
    }

    private static string ShortType(string? t)
        => t?.Split('/').LastOrDefault() ?? t ?? "Unknown";

    #endregion

    #region Supporting Helpers: Free-Tier Knowledge Base

    private record FreeTierEntry(string Label, string? FreeSku, string FreeSkuLabel, string[] PaidSkus, string Note);

    private static readonly Dictionary<string, FreeTierEntry> FreeTierMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Web/sites"] = new("App Service", "F1", "Free (F1)", ["B1", "B2", "B3", "S1", "S2", "S3", "P1V2", "P2V2", "P3V2"], "F1 provides 60 CPU-min/day."),
        ["Microsoft.Web/serverFarms"] = new("App Service Plan", "F1", "Free (F1)", ["B1", "B2", "B3", "S1", "S2", "S3"], "Downgrade to F1 if traffic is low."),
        ["Microsoft.Web/staticSites"] = new("Static Web App", "Free", "Free", ["Standard"], "Free tier: 100 GB bandwidth/month."),
        ["Microsoft.App/containerApps"] = new("Container App", null, "180k vCPU-s free/month", ["Consumption"], "Set min-replicas=0 to stay in free quota."),
        ["Microsoft.ContainerRegistry/registries"] = new("Container Registry", null, "No free tier", ["Basic", "Standard", "Premium"], "Basic ~$5/mo. Consider ghcr.io for free private images."),
        ["Microsoft.DocumentDB/databaseAccounts"] = new("Cosmos DB", "Free", "Free tier (1000 RU/s + 25 GB)", ["Standard"], "One free Cosmos DB per subscription."),
        ["Microsoft.Sql/servers/databases"] = new("Azure SQL", "Free", "Free offer (32 GB serverless)", ["Basic", "Standard", "Premium"], "One free Azure SQL per subscription."),
        ["Microsoft.Storage/storageAccounts"] = new("Storage Account", null, "5 GB Blob free/month (12 mo)", ["Standard_LRS", "Standard_GRS"], "Use LRS for lowest cost."),
        ["Microsoft.CognitiveServices/accounts"] = new("Azure AI / Cognitive", "F0", "Free (F0)", ["S0", "S1"], "F0 sufficient for dev/hobby use."),
        ["Microsoft.Search/searchServices"] = new("Azure AI Search", "free", "Free (1 svc, 3 indexes, 50 MB)", ["basic", "standard"], "One free search service per subscription."),
        ["microsoft.insights/components"] = new("Application Insights", null, "5 GB/month free ingestion", ["pergb2018"], "Enable adaptive sampling to stay under 5 GB/month."),
        ["Microsoft.OperationalInsights/workspaces"] = new("Log Analytics", "Free", "Free (500 MB/day)", ["PerGB2018", "Standard"], "Set a data cap on paid SKUs."),
        ["Microsoft.KeyVault/vaults"] = new("Key Vault", null, "~$0.03 per 10k ops", ["standard", "premium"], "Consolidate vaults when possible."),
        ["Microsoft.Network/publicIPAddresses"] = new("Public IP", null, "First 5 Basic static IPs free", ["Standard"], "Delete IPs not attached to any resource."),
        ["Microsoft.ServiceBus/namespaces"] = new("Service Bus", null, "No free tier — Basic ~$0.05/M ops", ["Basic", "Standard", "Premium"], "Use Basic if only simple queues needed."),
        ["Microsoft.SignalRService/SignalR"] = new("SignalR", "Free", "Free (20 connections)", ["Standard"], "Free tier: 20 concurrent connections."),
    };

    #endregion

    #region Supporting Helpers: GitHub Workflow Correlation

    private async Task<List<InfraReview>> LoadInfraReviewsForCorrelationAsync(
        List<RawService> services, CancellationToken ct)
    {
        try
        {
            var pat = _config["GitHub:PersonalAccessToken"];
            if (string.IsNullOrWhiteSpace(pat))
            {
                // Try gh CLI token
                try
                {
                    using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "gh",
                        Arguments = "auth token",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    });
                    if (proc is not null)
                    {
                        var token = (await proc.StandardOutput.ReadToEndAsync(ct)).Trim();
                        if (!string.IsNullOrWhiteSpace(token))
                            pat = token;
                    }
                }
                catch { /* gh not available */ }
            }

            if (string.IsNullOrWhiteSpace(pat))
                return [];

            var http = _httpClientFactory.CreateClient("github");
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", pat);

            return await FetchInfraReviewsAsync(http, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub workflow correlation skipped");
            return [];
        }
    }

    private async Task<List<InfraReview>> FetchInfraReviewsAsync(HttpClient http, CancellationToken ct)
    {
        var reviews = new List<InfraReview>();
        var page = 1;

        while (true)
        {
            var url = $"https://api.github.com/user/repos?visibility=all&affiliation=owner&per_page=100&page={page}";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                break;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                break;

            var repos = doc.RootElement.EnumerateArray().ToList();
            if (repos.Count == 0)
                break;

            foreach (var repo in repos)
            {
                var fullName = repo.GetProperty("full_name").GetString() ?? "";
                var repoUrl = repo.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
                var defaultBranch = repo.TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main";

                var (runStatus, runConclusion, runCompleted, runUrl, runName) =
                    await FetchLatestWorkflowRunAsync(http, fullName, defaultBranch, ct);

                reviews.Add(new InfraReview
                {
                    RepoName = repo.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "",
                    RepoUrl = repoUrl,
                    DefaultBranch = defaultBranch,
                    LatestWorkflowRunStatus = runStatus,
                    LatestWorkflowRunConclusion = runConclusion,
                    LatestWorkflowRunCompletedAt = runCompleted,
                    LatestWorkflowRunUrl = runUrl,
                    LatestWorkflowRunName = runName,
                });
            }

            if (repos.Count < 100)
                break;

            page++;
        }

        return reviews;
    }

    private async Task<(string? status, string? conclusion, DateTime? completedAt, string? runUrl, string? runName)>
        FetchLatestWorkflowRunAsync(HttpClient http, string fullName, string defaultBranch, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{fullName}/actions/runs?branch={Uri.EscapeDataString(defaultBranch)}&per_page=1";
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, null, null, null, null);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0)
                return (null, null, null, null, null);

            var run = runs[0];
            var status = run.TryGetProperty("status", out var st) ? st.GetString() : null;
            var conclusion = run.TryGetProperty("conclusion", out var conc) ? conc.GetString() : null;
            var runUrl = run.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
            var runName = run.TryGetProperty("name", out var rn) ? rn.GetString() : null;

            DateTime? completedAt = run.TryGetProperty("updated_at", out var ua)
                && ua.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ua.GetString(), out var dt) ? dt : null;

            return (status, conclusion, completedAt, runUrl, runName);
        }
        catch
        {
            return (null, null, null, null, null);
        }
    }

    private static InfraReview? MatchServiceToInfraReview(RawService svc, List<InfraReview> reviews)
    {
        if (reviews.Count == 0)
            return null;

        // Direct match: repo name == service name
        var direct = reviews.FirstOrDefault(r =>
            r.RepoName.Equals(svc.Name, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;

        // Fuzzy match: service name contains repo name or vice versa
        var fuzzy = reviews.FirstOrDefault(r =>
            svc.Name.Contains(r.RepoName, StringComparison.OrdinalIgnoreCase) ||
            r.RepoName.Contains(svc.Name, StringComparison.OrdinalIgnoreCase));
        if (fuzzy is not null)
            return fuzzy;

        // Match by resource group name containing repo name
        var byRg = reviews.FirstOrDefault(r =>
            !string.IsNullOrEmpty(svc.ResourceGroup) &&
            svc.ResourceGroup.Contains(r.RepoName, StringComparison.OrdinalIgnoreCase));
        if (byRg is not null)
            return byRg;

        // Match by friendly name
        var byFriendly = reviews.FirstOrDefault(r =>
            !string.IsNullOrEmpty(svc.FriendlyName) &&
            (svc.FriendlyName.Contains(r.RepoName, StringComparison.OrdinalIgnoreCase) ||
             r.RepoName.Contains(svc.FriendlyName, StringComparison.OrdinalIgnoreCase)));
        return byFriendly;
    }

    #endregion
}
