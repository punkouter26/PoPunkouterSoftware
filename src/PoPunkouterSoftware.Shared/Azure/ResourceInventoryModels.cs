namespace PoPunkouterSoftware.Shared.Azure;

// Everything inventoried in the subscription, including the cleanup candidates (zombie apps and orphaned resources) surfaced on the dashboard.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

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

public record AiServiceInventoryItem
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? Kind { get; init; }
    public string? Sku { get; init; }
    public string? Endpoint { get; init; }
    public int DeploymentCount { get; init; }
    public List<string> Deployments { get; init; } = new();
    public string Recommendation { get; init; } = "";
    public string RiskLevel { get; init; } = "watch";
}

public record LogAnalyticsWorkspaceItem
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? Location { get; init; }
    public string? Sku { get; init; }
    public int? RetentionInDays { get; init; }
    public double? DailyQuotaGb { get; init; }
    public string Recommendation { get; init; } = "";
    public string RiskLevel { get; init; } = "watch";
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
