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
}
