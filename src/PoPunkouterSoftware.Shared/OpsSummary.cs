namespace PoPunkouterSoftware.Shared;

/// <summary>A compact, first-paint projection for the casual Azure status check.</summary>
public record OpsSummary
{
    public DateTime? GeneratedAt { get; init; }
    public bool IsStale { get; init; }
    public string SubscriptionName { get; init; } = "";
    public int TotalServices { get; init; }
    public int ActiveServices { get; init; }
    public int BrokenServices { get; init; }
    public int HealthPercent { get; init; }
    public int TotalResources { get; init; }
    public string CostFormatted { get; init; } = "$0.00";
    public int CleanupCandidates { get; init; }
    public int SecurityFindings { get; init; }
    /// <summary>Everything needing attention, including the staleness flag.</summary>
    public int AttentionCount { get; init; }

    /// <summary>
    /// The subset an operator can act on directly — <see cref="AttentionCount"/> minus the
    /// staleness flag. Rendered as the hero badge; kept as a server-computed field so the
    /// badge and the headline can never disagree about what they are counting.
    /// </summary>
    public int ActionableCount { get; init; }
    public List<OpsMetricPoint> FleetHealth { get; init; } = new();
    public List<OpsMetricPoint> CostDrivers { get; init; } = new();
    public List<OpsMetricPoint> ResponseTimes { get; init; } = new();
    public List<OpsMetricPoint> CostHistory { get; init; } = new();
    public List<string> AttentionItems { get; init; } = new();
}

public record OpsMetricPoint(string Label, double Value);
