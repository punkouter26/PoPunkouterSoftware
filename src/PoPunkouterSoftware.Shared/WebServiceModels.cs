namespace PoPunkouterSoftware.Shared;

// The web-service fleet: discovery, reachability, 7-day metrics and free-tier state.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

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
