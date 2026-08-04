namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Canonical vocabularies for the string-typed domain concepts that flow through the wire
/// contract and Table Storage. The wire/storage format stays plain strings (changing it would
/// silently reinterpret every persisted gzipped report), but all producers and comparisons
/// route through these members so a typo'd status or an accidental alphabetical severity sort
/// becomes a compile-time/code-review problem instead of a silent logic bug.
/// </summary>
public static class ServiceHealth
{
    public const string Active = "active";
    public const string Broken = "broken";
    public const string Unreachable = "unreachable";
    public const string Unknown = "unknown";

    public static bool IsHealthy(string? status) =>
        string.Equals(status, Active, StringComparison.OrdinalIgnoreCase);

    public static bool IsBroken(string? status) =>
        string.Equals(status, Broken, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, Unreachable, StringComparison.OrdinalIgnoreCase);
}

public static class SeverityLevel
{
    public const string Critical = "critical";
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";

    /// <summary>Numeric ordering key — most severe first. Unknown values sort last.</summary>
    public static int Rank(string? severity) => severity?.ToLowerInvariant() switch
    {
        Critical => 0,
        High => 1,
        Medium => 2,
        Low => 3,
        _ => 4,
    };
}

public static class ResourceRiskLevel
{
    public const string Cleanup = "cleanup";
    public const string Cost = "cost";
    public const string Watch = "watch";
    public const string Ok = "ok";

    /// <summary>
    /// Numeric ordering key — most actionable first (cleanup &gt; cost &gt; watch &gt; ok).
    /// Sorting the raw strings alphabetically happened to produce this order by accident;
    /// this makes the semantics explicit and typo-proof.
    /// </summary>
    public static int Rank(string? level) => level?.ToLowerInvariant() switch
    {
        Cleanup => 0,
        Cost => 1,
        Watch => 2,
        Ok => 3,
        _ => 4,
    };
}

public static class IncidentTypes
{
    public const string NewIncident = "new-incident";
    public const string Recovery = "recovery";
}
