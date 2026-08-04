namespace PoPunkouterSoftware.Shared;

// Per-scan history summaries for the trend charts, and the incident log built from health transitions.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

/// <summary>Lightweight per-scan summary used for time-series charts on the Details page.</summary>
public record HistorySummary
{
    public DateTime GeneratedAt { get; init; }
    public int TotalServices { get; init; }
    public int ActiveServices { get; init; }
    public int BrokenServices { get; init; }
    public double TotalCost30Days { get; init; }
    public double ProjectedMonthCost { get; init; }
    public double AvgResponseTimeMs { get; init; }
    public int Total5xxErrors { get; init; }
    public int TotalResources { get; init; }
    public long ScanDurationMs { get; init; }
    public int? BrokenDelta { get; init; }
    public List<ServiceHistoryPoint> Services { get; init; } = new();
}

/// <summary>Per-service snapshot within a single <see cref="HistorySummary"/> entry.</summary>
public record ServiceHistoryPoint
{
    public string Name { get; init; } = "";
    public string HttpStatus { get; init; } = "";
    public int ResponseTimeMs { get; init; }
    public int Requests7d { get; init; }
}

// ─── Feature #9: Incident Log ─────────────────────────────────────────────────

/// <summary>A single service health transition event detected during a report refresh.</summary>
public record IncidentEntry
{
    public string ServiceName { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    /// <summary>"new-incident" (active→broken) or "recovery" (broken→active).</summary>
    public string Type { get; init; } = "";
    public DateTime OccurredAt { get; init; }
    public string? PreviousStatus { get; init; }
    public string? CurrentStatus { get; init; }
}

// ─── Plan recommendation ────────────────────────────────────────────────────
