namespace PoPunkouterSoftware;

/// <summary>
/// The two name-based exclusions every read contract has to apply, in one place.
///
/// <para>This lived as private state on <see cref="PortfolioEndpoints"/>, which is why
/// <c>/api/diag/summary</c> never applied it and reported "PoPunkouterSoftware is
/// unavailable" — this site, in its own attention list, while serving the page that
/// rendered the claim.</para>
///
/// <para>Dropping a catalog entry is never sufficient on its own: the portfolio and the
/// ops summary are both built from the Azure scan as well as from apps.json, and these
/// resources really do exist in the subscription, so they return through the inventory
/// path no matter what the catalog says.</para>
/// </summary>
internal static class PortfolioIdentity
{
    /// <summary>
    /// This site itself, which must never appear as a card in its own portfolio nor as a
    /// finding in its own dashboard.
    /// </summary>
    /// <remarks>
    /// Entries are already in <see cref="NormalizeName"/> form (letters and digits only,
    /// lower-cased), so both the Azure resource name "app-popunkoutersoftware" and the
    /// friendly name "PoPunkouterSoftware" collapse onto one entry here.
    /// </remarks>
    private static readonly HashSet<string> SelfIdentities =
        new(StringComparer.Ordinal) { "apppopunkoutersoftware", "popunkoutersoftware" };

    /// <summary>
    /// Apps that have been decommissioned. Their Azure resources may linger in the scan
    /// (and their DNS may already be gone), but they must not be showcased as live work.
    ///
    /// <para>PoRepoLineTracker's host stopped resolving entirely — the card shipped
    /// pointing at a dead name, which is the "ghost fleet" defect
    /// <c>Portfolio_EveryCardUrl_ActuallyResolvesAndResponds</c> exists to catch.</para>
    /// </summary>
    private static readonly HashSet<string> RetiredIdentities =
        new(StringComparer.Ordinal) { "porepolinetracker", "appporepolinetracker" };

    /// <summary>True when any of the candidate names is this site.</summary>
    internal static bool IsSelf(params string?[] candidateNames) =>
        candidateNames.Any(n => SelfIdentities.Contains(NormalizeName(n)));

    /// <summary>True when any of the candidate names is this site or a retired app.</summary>
    internal static bool IsExcluded(params string?[] candidateNames) =>
        candidateNames.Any(n =>
        {
            var key = NormalizeName(n);
            return SelfIdentities.Contains(key) || RetiredIdentities.Contains(key);
        });

    /// <summary>Letters and digits only, lower-cased — the stable join key between the
    /// curated catalog and scanned Azure inventory.</summary>
    internal static string NormalizeName(string? value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
