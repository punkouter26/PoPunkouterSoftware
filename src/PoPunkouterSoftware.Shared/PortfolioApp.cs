namespace PoPunkouterSoftware.Shared;

/// <summary>
/// One stable home-page portfolio card. The catalog in apps.json is authoritative and
/// live Azure inventory decorates it with current reachability and deployment details.
/// </summary>
public record PortfolioApp
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public string Category { get; init; } = "";
    public string Status { get; init; } = "unknown";
    public DateTime? InventoryGeneratedAt { get; init; }
    public List<string>? Technologies { get; init; }
    public string? GithubRepo { get; init; }
    /// <summary>API path of the stored home-page screenshot; null when none captured yet.</summary>
    public string? ScreenshotUrl { get; init; }
}
