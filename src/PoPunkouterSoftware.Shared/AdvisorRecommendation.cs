namespace PoPunkouterSoftware.Shared;

/// <summary>
/// One Azure Advisor recommendation for the scanned subscription — Microsoft's own list of
/// cost, security and reliability fixes, read during the scan and ranked into the priority
/// queue beside this app's findings rather than shown as a second list.
///
/// GoF: Value Object — immutable data carrier, no behaviour.
/// </summary>
public record AdvisorRecommendation
{
    /// <summary>Cost | Security | HighAvailability | OperationalExcellence | Performance.</summary>
    public string Category { get; init; } = "";
    /// <summary>High | Medium | Low.</summary>
    public string Impact { get; init; } = "";
    public string Problem { get; init; } = "";
    public string? Solution { get; init; }
    /// <summary>Name of the affected resource (Advisor's impactedValue).</summary>
    public string ImpactedName { get; init; } = "";
    /// <summary>ARM type of the affected resource (Advisor's impactedField).</summary>
    public string? ImpactedType { get; init; }
    /// <summary>Estimated monthly saving for a Cost recommendation, when Advisor gives one.</summary>
    public double? MonthlySavings { get; init; }
}
