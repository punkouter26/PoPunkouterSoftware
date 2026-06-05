namespace PoPunkouterSoftware.Shared.Azure;

// Shared Azure infrastructure domain models used by both the server (API/SDK)
// and the client (Blazor WASM) for deserialising API responses.
// GoF: Value Object — all records are immutable data carriers with no behaviour.

public record AzureReport
{
    public DateTime? GeneratedAt { get; init; }
    public SubscriptionInfo? Subscription { get; init; }
    public WebServicesInfo? WebServices { get; init; }
    public CostInfo? Cost { get; init; }
    public FreeTierInfo? FreeTier { get; init; }
    public AllResourceSummaryInfo? AllResourceSummary { get; init; }
    public List<SslEntry>? SslExpiry { get; init; }
    public List<ConfigDriftItem>? ConfigDrift { get; init; }
    public List<StorageItem>? StorageInventory { get; init; }
    public AppsJsonDiffInfo? AppsJsonDiff { get; init; }
    public List<AppInsightsMetric>? AppInsightsMetrics { get; init; }
    public List<ZombieApp>? ZombieApps { get; init; }
    public List<OrphanedResource>? OrphanedResources { get; init; }
    public BurnRateInfo? BurnRate { get; init; }
    public List<StepTimingEntry>? StepTimings { get; init; }
    public List<AppServicePlanInventoryEntry> AppServicePlanInventory { get; init; } = new();
    public ReportDelta? Delta { get; init; }
    /// <summary>Root-cause analysis for each broken or unreachable App Service.</summary>
    public List<ServiceDowntimeDiagnosis>? DowntimeDiagnoses { get; init; }
    /// <summary>Plan tier recommendations for each analysed service — upgrade/downgrade/keep.</summary>
    public List<PlanRecommendation> PlanRecommendations { get; init; } = new();
}

public record SubscriptionInfo { public string Name { get; init; } = ""; }

public record WebServicesInfo
{
    public int Total { get; init; }
    public ByStatusInfo? ByStatus { get; init; }
    public List<WebService> Services { get; init; } = new();
}

public record ByStatusInfo { public int Active { get; init; } public int Broken { get; init; } public int Other { get; init; } }

public record WebService
{
    public string Name { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string ResourceType { get; init; } = "";
    public string? Kind { get; init; }
    public string Url { get; init; } = "";
    public string HttpStatus { get; init; } = "";
    public string? PlatformState { get; init; }
    public string? Description { get; init; }
    public string? ResourceId { get; init; }
    /// <summary>App Service Plan name — populated if service is a Microsoft.Web/sites resource.</summary>
    public string? AppServicePlan { get; init; }
    /// <summary>App Service Plan SKU — e.g. F1, B2, S1. Resolved from the serverFarm resource.</summary>
    public string? AppServicePlanSku { get; init; }
    public ConnectivityInfo? Connectivity { get; init; }
    public MetricsInfo? Metrics7Days { get; init; }
    public FreeTierCheckInfo? FreeTierCheck { get; init; }
}

public record ConnectivityInfo
{
    public bool Success { get; init; }
    public int ResponseTime { get; init; }
    public string? Error { get; init; }
    public bool? IsAzureErrorPage { get; init; }
}

public record MetricsInfo { public int Requests { get; init; } public int Http5xx { get; init; } public double AverageResponseTime { get; init; } }
public record FreeTierCheckInfo { public bool IsOnFreeTier { get; init; } public bool IsOnPaidTier { get; init; } public bool CanGoFree { get; init; } }

public record CostInfo
{
    public double TotalCost30Days { get; init; }
    public string? TotalFormatted { get; init; }
    public string? Note { get; init; }
    public List<CostDriver> TopCostDrivers { get; init; } = new();
}

public record CostDriver { public string Name { get; init; } = ""; public double Cost { get; init; } }

public record FreeTierInfo
{
    public List<FreeTierItem> OnFree { get; init; } = new();
    public List<FreeTierItem> CanGoFree { get; init; } = new();
}

public record FreeTierItem
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string CurrentSku { get; init; } = "";
    public string? FreeSku { get; init; }
    public string? FreeSkuLabel { get; init; }
    public string? ResourceGroup { get; init; }
    public string? Recommendation { get; init; }
}

public record AllResourceSummaryInfo
{
    public int Total { get; init; }
    public Dictionary<string, int> ByType { get; init; } = new();
    public Dictionary<string, List<ResourceDetail>> ResourcesByType { get; init; } = new();
}

public record ResourceDetail
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? Sku { get; init; }
    public string? Type { get; init; }
}

public record AppServicePlanInventoryEntry
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? Sku { get; init; }
    public int AppCount { get; init; }
    public string? Type { get; init; }
}

public record SslEntry
{
    public string Name { get; init; } = "";
    public string? Url { get; init; }
    public string? Expiry { get; init; }
    public int? DaysLeft { get; init; }
    public string? Subject { get; init; }
    public string? Error { get; init; }
}

public record ConfigDriftItem
{
    public string Name { get; init; } = "";
    public string? FriendlyName { get; init; }
    public string? ResourceGroup { get; init; }
    public int IssueCount { get; init; }
    public List<ConfigIssue>? Issues { get; init; }
}

public record ConfigIssue { public string Severity { get; init; } = ""; public string Issue { get; init; } = ""; }

public record StorageItem
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? Sku { get; init; }
    public bool PublicBlobAccess { get; init; }
    public bool HttpsOnly { get; init; }
    public string? MinTls { get; init; }
    public int IssueCount { get; init; }
    public List<StorageIssue>? Issues { get; init; }
}

public record StorageIssue { public string Severity { get; init; } = ""; public string Issue { get; init; } = ""; }

public record AppsJsonDiffInfo
{
    public int? CurrentCount { get; init; }
    public int? DiscoveredCount { get; init; }
    public List<string> NewApps { get; init; } = new();
    public List<string> RemovedApps { get; init; } = new();
    public List<string> UpdatedApps { get; init; } = new();
}

public record AppInsightsMetric
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public int? Requests7Days { get; init; }
    public int? FailedRequests7Days { get; init; }
    public int? Exceptions7Days { get; init; }
}

public record ZombieApp
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? HttpStatus { get; init; }
    public string? PlatformState { get; init; }
    public string? Recommendation { get; init; }
}

public record OrphanedResource
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string Type { get; init; } = "";
    public string Reason { get; init; } = "";
    public string? EstimatedMonthlyCost { get; init; }
    public string? Command { get; init; }
}

public record DailyCostEntry
{
    public string Date { get; init; } = "";
    public double Cost { get; init; }
}

public record BurnRateInfo
{
    public List<DailyCostEntry> DailyCosts { get; init; } = new();
    public double ProjectedMonthTotal { get; init; }
    public string? ProjectedFormatted { get; init; }
}

public record StepTimingEntry
{
    public string Step { get; init; } = "";
    public long ElapsedMs { get; init; }
}

// ─── Item 1: Report Delta / Trending ──────────────────────────────────────────

public record ReportDelta
{
    public DateTime? PreviousGeneratedAt { get; init; }
    public int? BrokenServicesDelta { get; init; }
    public double? CostDelta { get; init; }
    public List<string> NewBrokenServices { get; init; } = new();
    public List<string> RecoveredServices { get; init; } = new();
    public List<string> NewOrphanedResources { get; init; } = new();
}

// ─── Public Status Page models ────────────────────────────────────────────────

/// <summary>Data contract for the public /status page — safe to expose without auth.</summary>
public record StatusPageReport
{
    public DateTime GeneratedAt { get; init; }
    public List<ServiceStatusEntry> Services { get; init; } = new();
}

public record ServiceStatusEntry
{
    public string Name { get; init; } = "";
    public string? FriendlyName { get; init; }
    public string? Url { get; init; }
    /// <summary>Most recent HTTP status from the latest report: active | broken | unreachable | unknown</summary>
    public string CurrentStatus { get; init; } = "unknown";
    public int? ResponseTimeMs { get; init; }
    /// <summary>Historical samples from stored report history (newest first).</summary>
    public List<StatusSample> Samples { get; init; } = new();
}

public record StatusSample
{
    public DateTime At { get; init; }
    public string Status { get; init; } = "unknown";
    public int? ResponseTimeMs { get; init; }
}

// ─── CI/CD Infrastructure Review models ──────────────────────────────────────

/// <summary>Infrastructure and CI/CD review result for one GitHub repository.</summary>
public record InfraReview
{
    public string RepoName { get; init; } = "";
    public string? DefaultBranch { get; init; }
    public bool IsPrivate { get; init; }
    public string? RepoUrl { get; init; }
    /// <summary>Inferred primary hosting target: App Service, Static Web Apps, Container Apps, Functions, Unknown.</summary>
    public string DeploymentTarget { get; init; } = "Unknown";
    /// <summary>Inferred deploy method: GitHub Actions, Manual, Unknown.</summary>
    public string DeploymentMethod { get; init; } = "Unknown";
    public List<CiCdFileSummary> CiCdFiles { get; init; } = new();
    public List<InfraFileSummary> InfraFiles { get; init; } = new();
    public DateTime ScannedAt { get; init; }
    /// <summary>Non-null when scanning failed for this repo (permissions, rate limit, etc.).</summary>
    public string? Error { get; init; }
    /// <summary>Status of the most recent GitHub Actions workflow run (completed, in_progress, etc.).</summary>
    public string? LatestWorkflowRunStatus { get; init; }
    /// <summary>Conclusion of the most recent GitHub Actions workflow run (success, failure, cancelled).</summary>
    public string? LatestWorkflowRunConclusion { get; init; }
    /// <summary>When the most recent GitHub Actions workflow run completed.</summary>
    public DateTime? LatestWorkflowRunCompletedAt { get; init; }
    /// <summary>URL to the most recent GitHub Actions workflow run.</summary>
    public string? LatestWorkflowRunUrl { get; init; }
    /// <summary>Display title of the most recent workflow run.</summary>
    public string? LatestWorkflowRunName { get; init; }
}

/// <summary>Summary of a single GitHub Actions workflow file.</summary>
public record CiCdFileSummary
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    /// <summary>Workflow triggers extracted from the `on:` key, e.g. push, pull_request, workflow_dispatch.</summary>
    public List<string> Triggers { get; init; } = new();
    /// <summary>Azure deploy action ids found in the workflow, e.g. azure/webapps-deploy.</summary>
    public List<string> DeployActions { get; init; } = new();
    /// <summary>Branch filters extracted from push/pull_request triggers.</summary>
    public List<string> BranchFilters { get; init; } = new();
}

/// <summary>Summary of a single infrastructure definition file (Bicep, ARM, Docker, Azure Developer CLI).</summary>
public record InfraFileSummary
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    /// <summary>bicep | arm | docker | azd | compose</summary>
    public string FileType { get; init; } = "";
    /// <summary>Resource type strings extracted from Bicep files, e.g. Microsoft.Web/sites.</summary>
    public List<string> ResourceTypes { get; init; } = new();
}

// ─── Downtime root-cause diagnosis ───────────────────────────────────────────

/// <summary>
/// Per-service root-cause analysis performed for every broken or unreachable
/// App Service after connectivity testing.  Aggregates ARM state, App Service
/// Plan health, recent deployment results, and 48-hour activity-log events.
/// </summary>
public record ServiceDowntimeDiagnosis
{
    public string Name { get; init; } = "";
    public string? FriendlyName { get; init; }
    public string? ResourceGroup { get; init; }
    public string HttpStatus { get; init; } = "";
    /// <summary>Normal | Limited | DisasterRecoveryMode — from ARM site resource.</summary>
    public string? AvailabilityState { get; init; }
    /// <summary>Normal | Exceeded — indicates free-tier quota breach.</summary>
    public string? UsageState { get; init; }
    /// <summary>True when the app is currently quota-suspended.</summary>
    public bool IsSuspended { get; init; }
    public DateTime? SuspendedTill { get; init; }
    public string? PlanName { get; init; }
    /// <summary>Ready | Pending | Creating — from ARM server farm resource.</summary>
    public string? PlanStatus { get; init; }
    public string? PlanSku { get; init; }
    public bool PlanStopped { get; init; }
    public List<DeploymentEntry> RecentDeployments { get; init; } = new();
    public List<ActivityLogEntry> RecentActivity { get; init; } = new();
    public string LikelyCause { get; init; } = "Unknown";
    public string? SuggestedFix { get; init; }
    /// <summary>App Insights 7-day exception count for this service.</summary>
    public int? AppInsightsExceptions7Days { get; init; }
    /// <summary>App Insights 7-day failed request count for this service.</summary>
    public int? AppInsightsFailedRequests7Days { get; init; }
    /// <summary>URL to the latest GitHub Actions workflow run for this service's repo.</summary>
    public string? GitHubWorkflowRunUrl { get; init; }
    /// <summary>Latest GitHub Actions workflow run status (completed, in_progress, etc.).</summary>
    public string? GitHubWorkflowStatus { get; init; }
    /// <summary>Latest GitHub Actions workflow run conclusion (success, failure, cancelled, etc.).</summary>
    public string? GitHubWorkflowConclusion { get; init; }
    /// <summary>When the latest GitHub workflow run completed.</summary>
    public DateTime? GitHubWorkflowCompletedAt { get; init; }
    /// <summary>Kudu process diagnostics — list of running processes or error.</summary>
    public string? KuduProcesses { get; init; }
    /// <summary>True when the Kudu SCM site was reachable.</summary>
    public bool KuduReachable { get; init; }
}

/// <summary>One entry from the Kudu/ARM deployment history of an App Service.</summary>
public record DeploymentEntry
{
    public string? DeploymentId { get; init; }
    public bool? Active { get; init; }
    public int? StatusCode { get; init; }
    /// <summary>Success | Failed | Deploying | Building | Pending</summary>
    public string? StatusText { get; init; }
    public string? Message { get; init; }
    public DateTime? DeployedAt { get; init; }
    public string? Author { get; init; }
}

/// <summary>One event from the Azure Activity Log (last 48 hours) for a broken service.</summary>
public record ActivityLogEntry
{
    public string? OperationName { get; init; }
    public string? Status { get; init; }
    public DateTime? EventTimestamp { get; init; }
    public string? Caller { get; init; }
    public string? Level { get; init; }
}

// ─── History summary (for /timebased page time-series charts) ───────────────

/// <summary>Lightweight per-scan summary used for time-series charts on the Details page.</summary>
public record HistorySummary
{
    public DateTime GeneratedAt { get; init; }
    public int TotalServices { get; init; }
    public int ActiveServices { get; init; }
    public int BrokenServices { get; init; }
    public double TotalCost30Days { get; init; }
    public double ProjectedMonthCost { get; init; }
    public double AvgResponseTimeMs { get; init; }
    public int Total5xxErrors { get; init; }
    public int TotalResources { get; init; }
    public long ScanDurationMs { get; init; }
    public int? BrokenDelta { get; init; }
    public List<ServiceHistoryPoint> Services { get; init; } = new();
}

/// <summary>Per-service snapshot within a single <see cref="HistorySummary"/> entry.</summary>
public record ServiceHistoryPoint
{
    public string Name { get; init; } = "";
    public string HttpStatus { get; init; } = "";
    public int ResponseTimeMs { get; init; }
    public int Requests7d { get; init; }
}

// ─── Feature #9: Incident Log ─────────────────────────────────────────────────

/// <summary>A single service health transition event detected during a report refresh.</summary>
public record IncidentEntry
{
    public string ServiceName { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    /// <summary>"new-incident" (active→broken) or "recovery" (broken→active).</summary>
    public string Type { get; init; } = "";
    public DateTime OccurredAt { get; init; }
    public string? PreviousStatus { get; init; }
    public string? CurrentStatus { get; init; }
}

// ─── Plan recommendation ────────────────────────────────────────────────────

/// <summary>Recommendation for whether a service should change its App Service Plan tier.</summary>
public record PlanRecommendation
{
    public string ServiceName { get; init; } = "";
    public string? FriendlyName { get; init; }
    public string? ResourceGroup { get; init; }
    public string? CurrentPlanName { get; init; }
    public string CurrentPlanSku { get; init; } = "Unknown";
    public string RecommendedPlanSku { get; init; } = "";
    public string Action { get; init; } = "keep"; // upgrade | downgrade | keep
    public string Reason { get; init; } = "";
    public string? MonthlyCostImpact { get; init; }
    public string Priority { get; init; } = "low"; // high | medium | low
    public List<string> Triggers { get; init; } = new();
    /// <summary>Estimated monthly cost of the recommended plan.</summary>
    public string? RecommendedMonthlyCost { get; init; }
    /// <summary>Estimated monthly cost of the current plan.</summary>
    public string? CurrentMonthlyCost { get; init; }
}
