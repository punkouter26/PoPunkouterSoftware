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
    // New audit fields
    public ReportDelta? Delta { get; init; }
    public AlertsAuditInfo? AlertsAudit { get; init; }
    public AutoScaleAuditInfo? AutoScaleAudit { get; init; }
    public BackupAuditInfo? BackupAudit { get; init; }
    public DeploymentSlotsInfo? DeploymentSlots { get; init; }
    public DiagnosticCoverageInfo? DiagnosticCoverage { get; init; }
    public CriticalFindingsInfo? CriticalFindings { get; init; }
    public RbacAuditInfo? RbacAudit { get; init; }
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
    public List<FreeTierItem>? NoFreeTier { get; init; }
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

public record SafeToRemoveItem
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Confidence { get; init; } = "";
    public string? Command { get; init; }
    public string? Saving { get; init; }
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

// ─── Item 3: Alert Rules Audit ────────────────────────────────────────────────

public record AlertsAuditInfo
{
    public int TotalAlertRules { get; init; }
    public List<ServiceAlertStatus> ServicesWithoutAlerts { get; init; } = new();
}

public record ServiceAlertStatus
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public bool HasAlerts { get; init; }
    public int AlertCount { get; init; }
}

// ─── Item 4: Auto-Scale Audit ─────────────────────────────────────────────────

public record AutoScaleAuditInfo
{
    public List<AutoScaleItem> WithAutoScale { get; init; } = new();
    public List<AutoScaleItem> WithoutAutoScale { get; init; } = new();
}

public record AutoScaleItem
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? Sku { get; init; }
    public bool HasAutoScale { get; init; }
}

// ─── Item 5: Backup Audit ─────────────────────────────────────────────────────

public record BackupAuditInfo
{
    public List<BackupItem> WithBackup { get; init; } = new();
    public List<BackupItem> WithoutBackup { get; init; } = new();
}

public record BackupItem
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public bool HasBackup { get; init; }
    public string? BackupFrequency { get; init; }
    public string? Retention { get; init; }
}

// ─── Item 6: Deployment Slots ─────────────────────────────────────────────────

public record DeploymentSlotsInfo
{
    public int TotalSlots { get; init; }
    public List<SlotEntry> Slots { get; init; } = new();
}

public record SlotEntry
{
    public string AppName { get; init; } = "";
    public string SlotName { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? State { get; init; }
    public bool IsAlwaysOn { get; init; }
    public string? Url { get; init; }
}

// ─── Item 7: Diagnostic Settings Coverage ────────────────────────────────────

public record DiagnosticCoverageInfo
{
    public int TotalResources { get; init; }
    public int ResourcesWithDiagnostics { get; init; }
    public List<DiagnosticEntry> WithoutDiagnostics { get; init; } = new();
}

public record DiagnosticEntry
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string Type { get; init; } = "";
}

// ─── Item 9: Critical Findings / Severity Score ───────────────────────────────

public record CriticalFindingsInfo
{
    public int SeverityScore { get; init; }
    public string SeverityLabel { get; init; } = "Healthy";
    public List<CriticalFinding> Findings { get; init; } = new();
}

public record CriticalFinding
{
    public string Severity { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Resource { get; init; }
}

// ─── Item 10: RBAC Over-Permission Audit ─────────────────────────────────────

public record RbacAuditInfo
{
    public List<RbacOverpermission> OverprivilegedAssignments { get; init; } = new();
}

public record RbacOverpermission
{
    public string PrincipalId { get; init; } = "";
    public string? PrincipalName { get; init; }
    public string Role { get; init; } = "";
    public string Scope { get; init; } = "";
    public string? PrincipalType { get; init; }
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
    /// <summary>Matched friendly app name from apps.json (if found).</summary>
    public string? AppName { get; init; }
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
