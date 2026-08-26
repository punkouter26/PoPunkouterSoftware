namespace PoPunkouterSoftware.Shared;

/// <summary>
/// What moved between the two most recent scans — the "what changed" answer the dashboard
/// leads with. Computed server-side from consecutive <see cref="HistorySummary"/> rows plus
/// the current report, so the first-paint contract carries it without shipping the full
/// report graph or making the client diff anything.
///
/// GoF: Value Object — immutable data carrier, no behaviour.
/// </summary>
public record ScanDelta
{
    /// <summary>When the scan this delta describes ran.</summary>
    public DateTime? CurrentScanAt { get; init; }

    /// <summary>When the scan it is compared against ran. Null when there is no prior scan.</summary>
    public DateTime? PreviousScanAt { get; init; }

    /// <summary>
    /// False when there is no prior scan to compare against (first ever scan, or history
    /// pruned). The UI renders a "baseline" state rather than an empty change list, which
    /// would read as "nothing changed".
    /// </summary>
    public bool HasBaseline { get; init; }

    /// <summary>Ordered most-significant-first; empty means genuinely nothing moved.</summary>
    public List<ScanChange> Changes { get; init; } = new();
}

/// <summary>One discrete difference between two scans.</summary>
/// <param name="Kind">
/// One of <see cref="ScanChangeKinds"/>. Drives the icon and colour; the client must not
/// parse <paramref name="Text"/> to work out what happened.
/// </param>
/// <param name="Direction">
/// <c>"better"</c>, <c>"worse"</c> or <c>"neutral"</c> — whether this change is good news.
/// Kept separate from <paramref name="Kind"/> because the same kind goes both ways (cost can
/// fall as well as rise).
/// </param>
/// <param name="Text">A complete human-readable sentence fragment, e.g. "PoSeeReview came back online".</param>
/// <param name="Detail">Optional supporting numbers, e.g. "was 502, now 200".</param>
public record ScanChange(string Kind, string Direction, string Text, string? Detail = null);

/// <summary>Canonical <see cref="ScanChange.Kind"/> values — see <see cref="ServiceHealth"/> for the pattern.</summary>
public static class ScanChangeKinds
{
    public const string ServiceDown = "service-down";
    public const string ServiceRecovered = "service-recovered";
    public const string Cost = "cost";
    public const string Security = "security";
    public const string Cleanup = "cleanup";
    public const string Resources = "resources";
    public const string Performance = "performance";
}

/// <summary>Canonical <see cref="ScanChange.Direction"/> values.</summary>
public static class ScanChangeDirection
{
    public const string Better = "better";
    public const string Worse = "worse";
    public const string Neutral = "neutral";
}
