namespace PoPunkouterSoftware.Shared;

// Spend: 30-day totals, top drivers, the daily series, burn rate, and free-tier headroom.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

public record CostInfo
{
    public double TotalCost30Days { get; init; }
    public string? TotalFormatted { get; init; }
    public string? Note { get; init; }
    public List<CostDriver> TopCostDrivers { get; init; } = new();
}

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
