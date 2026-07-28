namespace PoPunkouterSoftware.Shared.Azure;

// Security and configuration findings: SSL expiry and per-service configuration drift.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

public record SslEntry
{
    public string Name { get; init; } = "";
    public string? Url { get; init; }
    public string? Expiry { get; init; }
    public int? DaysLeft { get; init; }
    public string? Subject { get; init; }
    public string? Error { get; init; }
}

public record ConfigDriftItem
{
    public string Name { get; init; } = "";
    public string? FriendlyName { get; init; }
    public string? ResourceGroup { get; init; }
    public int IssueCount { get; init; }
    public List<ConfigIssue>? Issues { get; init; }
}

public record ConfigIssue { public string Severity { get; init; } = ""; public string Issue { get; init; } = ""; }
