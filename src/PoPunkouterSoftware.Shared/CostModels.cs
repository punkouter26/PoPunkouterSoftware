namespace PoPunkouterSoftware.Shared;

// Spend: 30-day totals, top drivers, the daily series, and burn rate.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

public record CostInfo
{
    public double TotalCost30Days { get; init; }
    public string? TotalFormatted { get; init; }
    public string? Note { get; init; }
    public List<CostDriver> TopCostDrivers { get; init; } = new();

    /// <summary>
    /// Per-resource-group daily spend over the same 30 days. Feeds the spend-spike detector
    /// and the cost-per-person join on /users. Empty for reports saved before it existed.
    /// </summary>
    public List<ResourceGroupCost> ResourceGroups { get; init; } = new();
}

public record ResourceGroupCost
{
    public string Name { get; init; } = "";
    public double Total { get; init; }
    /// <summary>Only days with non-zero spend — Cost Management omits the rest.</summary>
    public List<DailyCostEntry> Daily { get; init; } = new();
}

/// <summary>A resource group whose latest day of spend is far outside its own recent pattern.</summary>
/// <param name="Day">yyyy-MM-dd, the latest day Cost Management reported.</param>
/// <param name="Baseline">Mean daily spend over the preceding baseline window.</param>
public record CostAnomaly(string ResourceGroup, string Day, double Cost, double Baseline);

public record CostDriver { public string Name { get; init; } = ""; public double Cost { get; init; } }

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

