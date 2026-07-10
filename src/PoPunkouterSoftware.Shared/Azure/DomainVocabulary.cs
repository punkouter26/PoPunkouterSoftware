using System.Text.RegularExpressions;

namespace PoPunkouterSoftware.Shared.Azure;

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

/// <summary>
/// Strongly-typed GitHub repository identifier. A raw "owner/repo" string travels from an
/// unauthenticated query parameter into a GitHub API URL path — this type makes the
/// "validated before use" guarantee travel with the value instead of relying on every
/// call site remembering to run the regex.
/// </summary>
public readonly partial record struct RepoId(string Owner, string Name)
{
    public static bool TryParse(string? value, out RepoId result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var match = RepoPattern().Match(value);
        if (!match.Success)
            return false;

        result = new RepoId(match.Groups[1].Value, match.Groups[2].Value);
        return true;
    }

    public override string ToString() => $"{Owner}/{Name}";

    // Mirrors RepoQueryValidator.Pattern — each segment limited to GitHub's name characters.
    [GeneratedRegex(@"^([a-zA-Z0-9_.\-]+)/([a-zA-Z0-9_.\-]+)$")]
    private static partial Regex RepoPattern();
}
