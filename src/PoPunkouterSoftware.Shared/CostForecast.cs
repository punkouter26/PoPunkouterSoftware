namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Month-end spend projection measured against an optional budget — the "$12.08" number on
/// its own says nothing, "on pace for $38 of your $40" says everything.
///
/// <para>The projection itself is not re-derived here: <c>AzureReport.BurnRate</c> already
/// computes <c>ProjectedMonthTotal</c> during the scan and it is persisted per scan on
/// <see cref="HistorySummary.ProjectedMonthCost"/>. This record only relates that figure to
/// the configured budget and to the previous scan's projection.</para>
///
/// GoF: Value Object — immutable data carrier, no behaviour.
/// </summary>
public record CostForecast
{
    /// <summary>Spend so far in the current 30-day window.</summary>
    public double SpentToDate { get; init; }

    /// <summary>Projected total for the month, from the scan's burn-rate step.</summary>
    public double ProjectedMonthTotal { get; init; }

    /// <summary>
    /// The configured monthly ceiling (<c>Budget:MonthlyUsd</c>), or null when no budget is
    /// set — in which case the UI shows the projection without a ring or a verdict.
    /// </summary>
    public double? BudgetUsd { get; init; }

    /// <summary>
    /// Projection as a percentage of budget, clamped to 0 and left uncapped above 100 so an
    /// overrun is visible as a number even though the ring stops drawing at full.
    /// Null when no budget is configured.
    /// </summary>
    public int? PercentOfBudget { get; init; }

    /// <summary>
    /// <c>"under"</c>, <c>"near"</c> (≥80%), <c>"over"</c> (&gt;100%), or <c>"unknown"</c>
    /// when no budget is configured. Server-computed so the ring colour and the wording can
    /// never disagree about which band the projection is in.
    /// </summary>
    public string Verdict { get; init; } = CostVerdict.Unknown;

    /// <summary>
    /// Change in the projection since the previous scan, in dollars. Null when there is no
    /// prior scan to compare against.
    /// </summary>
    public double? ProjectionDelta { get; init; }
}

/// <summary>Canonical <see cref="CostForecast.Verdict"/> values.</summary>
public static class CostVerdict
{
    public const string Under = "under";
    public const string Near = "near";
    public const string Over = "over";
    public const string Unknown = "unknown";

    /// <summary>The band a projection falls in. Pure — no I/O, no config lookup.</summary>
    public static string For(double projected, double? budget)
    {
        if (budget is not > 0)
            return Unknown;
        var pct = projected / budget.Value;
        return pct > 1.0 ? Over : pct >= 0.8 ? Near : Under;
    }
}
