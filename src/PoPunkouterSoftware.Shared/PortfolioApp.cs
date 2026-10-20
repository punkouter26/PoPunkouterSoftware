namespace PoPunkouterSoftware.Shared;

/// <summary>
/// One stable home-page portfolio card. The catalog in apps.json is authoritative and
/// live Azure inventory decorates it with current reachability.
///
/// <para>Deliberately narrow: this is exactly what <c>PortfolioAppCard</c> binds, plus
/// <see cref="Status"/>, which the API contract tests pin. <c>Category</c>,
/// <c>InventoryGeneratedAt</c>, <c>Technologies</c> and <c>GithubRepo</c> were serialised
/// onto every card in the response and read by nothing after the design pass stripped the
/// card down to screenshot + name + description + link. They stay in apps.json as catalog
/// metadata; they no longer ride the wire.</para>
/// </summary>
public record PortfolioApp
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public string Status { get; init; } = "unknown";
    /// <summary>API path of the stored home-page screenshot; null when none captured yet.</summary>
    public string? ScreenshotUrl { get; init; }
    /// <summary>Latest nightly PageSpeed Insights scores; null until one has been captured.</summary>
    public LighthouseScores? Scores { get; init; }
    /// <summary>
    /// Observed days since this app's last down or degraded day on the 30-day uptime grid; 30
    /// means every day of the window was seen and clean. Null when the app was never observed — no streak to claim.
    /// </summary>
    public int? CleanDays { get; init; }
}
