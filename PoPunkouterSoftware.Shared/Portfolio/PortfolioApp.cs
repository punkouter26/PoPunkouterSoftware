namespace PoPunkouterSoftware.Shared.Portfolio;

/// <summary>
/// One home-page portfolio card: a live (HTTP-active) Azure web service merged with its
/// presentation metadata (description, technologies, GitHub repo) from apps.json.
/// The merge happens server-side in /api/portfolio so the client just renders the list.
/// </summary>
public record PortfolioApp
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public List<string>? Technologies { get; init; }
    public string? GithubRepo { get; init; }
    /// <summary>API path of the stored home-page screenshot; null when none captured yet.</summary>
    public string? ScreenshotUrl { get; init; }
}
