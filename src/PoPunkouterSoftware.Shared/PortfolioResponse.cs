namespace PoPunkouterSoftware.Shared;

/// <summary>
/// Envelope for GET /api/portfolio: the cards plus the inventory freshness the UI needs
/// to be honest about what "Live" means (a badge from a two-week-old scan is not "Live").
/// </summary>
public record PortfolioResponse
{
    /// <summary>When the Azure inventory behind the cards was generated; null when no report exists.</summary>
    public DateTime? GeneratedAt { get; init; }

    /// <summary>True when the inventory is older than <see cref="PortfolioFreshness.StaleAfter"/> (or missing).</summary>
    public bool Stale { get; init; }

    /// <summary>True while a background inventory refresh is running server-side.</summary>
    public bool RefreshInProgress { get; init; }

    /// <summary>Server build fingerprint — lets the client detect stale-WASM-but-fresh-API state.</summary>
    public long BuildId { get; init; }

    public List<PortfolioApp> Apps { get; init; } = new();
}

/// <summary>
/// Single source of truth for "how old may inventory data be before we stop trusting it" —
/// shared by the portfolio endpoint, the ops summary, and the client badge rendering.
/// </summary>
public static class PortfolioFreshness
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    /// <summary>Missing data is stale by definition — it can never be trusted as current.</summary>
    public static bool IsStale(DateTime? generatedAtUtc, DateTime utcNow) =>
        generatedAtUtc is not DateTime generated || utcNow - generated > StaleAfter;
}
