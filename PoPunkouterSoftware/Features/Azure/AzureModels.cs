namespace PoPunkouterSoftware.Features.Azure;

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
