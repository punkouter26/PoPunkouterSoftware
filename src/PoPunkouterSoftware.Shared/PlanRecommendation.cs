namespace PoPunkouterSoftware.Shared;

// App Service Plan tier recommendation for one service.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

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
