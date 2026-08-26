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
    public List<OpsMetricPoint> CostDrivers { get; init; } = new();
    public List<OpsMetricPoint> ResponseTimes { get; init; } = new();
    public List<OpsMetricPoint> CostHistory { get; init; } = new();
    public List<string> AttentionItems { get; init; } = new();

    // ─── Trend series behind the KPI-tile sparklines ─────────────────────────
    // Each is oldest-first over the same history window as CostHistory, so the four hero
    // tiles show a shape rather than a bare number. Projected from the precomputed
    // HistorySummary rows — no extra storage, no extra Azure calls. A series with fewer
    // than two points is not renderable as a trend; the client hides the sparkline rather
    // than drawing a single dot.

    /// <summary>Health percentage per scan.</summary>
    public List<OpsMetricPoint> HealthHistory { get; init; } = new();

    /// <summary>Unavailable-service count per scan.</summary>
    public List<OpsMetricPoint> BrokenHistory { get; init; } = new();

    /// <summary>Total resource count per scan.</summary>
    public List<OpsMetricPoint> ResourceHistory { get; init; } = new();

    /// <summary>
    /// What moved since the previous scan. Never null — a first scan returns a delta with
    /// <see cref="ScanDelta.HasBaseline"/> false so the UI can say "baseline" instead of
    /// silently implying nothing changed.
    /// </summary>
    public ScanDelta Changes { get; init; } = new();

    /// <summary>Month-end spend projection against the configured budget.</summary>
    public CostForecast Forecast { get; init; } = new();

    /// <summary>30-day uptime grid built from the per-service snapshots in history.</summary>
    public UptimeHeatmap Uptime { get; init; } = new();

    /// <summary>
    /// The precomputed AI (or rule-based) triage paragraph for the latest scan — projected
    /// straight from <see cref="AzureReport.AiSummary"/> so the first-paint contract carries
    /// it without shipping the full report graph. Null before any scan has populated it.
    /// </summary>
    public AiSummaryResult? AiSummary { get; init; }
}

public record OpsMetricPoint(string Label, double Value);
