namespace PoPunkouterSoftware.Shared;

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

/// <summary>
/// One entry from <c>apps.json</c> that no longer matches reality: either the Azure
/// resource no longer exists, or the URL no longer serves anything reachable.
/// Surfaced as a cleanup candidate — the catalog itself stays unchanged (a probe
/// failure must not blank a card) and the human still owns the JSON edit.
/// </summary>
public record CatalogDriftItem
{
    public string Name { get; init; } = "";
    public string? Url { get; init; }
    /// <summary>"missing-in-azure" or "url-not-loadable".</summary>
    public string Kind { get; init; } = "";
    /// <summary>Short human-readable reason the dashboard renders as the Reason line.</summary>
    public string Reason { get; init; } = "";
    /// <summary>"high" (missing in Azure) or "medium" (URL no longer loads).</summary>
    public string Confidence { get; init; } = "";
    /// <summary>The exact <c>apps.json</c> JSON to delete. Owner pastes it to remove the entry.</summary>
    public string? RemovalSnippet { get; init; }
}

public record AppInsightsMetric
{
    public string Name { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public int? Requests7Days { get; init; }
    public int? FailedRequests7Days { get; init; }
    public int? Exceptions7Days { get; init; }
}
