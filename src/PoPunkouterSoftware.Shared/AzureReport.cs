namespace PoPunkouterSoftware.Shared;

// The root report envelope: what one Azure scan produced, plus scan-level metadata (per-step timings and the run-over-run delta).
// GoF: Value Object - all records are immutable data carriers with no behaviour.

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
    public List<AiServiceInventoryItem> AiServicesInventory { get; init; } = new();
    public List<LogAnalyticsWorkspaceItem> LogAnalyticsInventory { get; init; } = new();
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

// ─── CI/CD Infrastructure Review models ──────────────────────────────────────
