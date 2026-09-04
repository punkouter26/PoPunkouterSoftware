using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Builds the human-readable "attention items" list from an <see cref="AzureReport"/> — the
/// same list rendered as <c>OpsSummary.AttentionItems</c> in the first-paint contract and fed
/// to <see cref="AiTriageService"/> as the triage model's input. Extracted out of
/// <c>DiagEndpoints.BuildOpsSummary</c> so the API's read-time projection and
/// <c>ReportRefreshRunner</c>'s scan-time AI precompute cannot drift from each other — both
/// now call this one method.
///
/// <para>Lives in Infrastructure (not API) so it can also be exercised from
/// <c>ReportRefreshRunner</c> without a slice-to-slice reference; the caller supplies the
/// name-exclusion predicate (<c>PortfolioIdentity.IsSelf</c> in API) rather than this
/// method depending on it directly, since <c>PortfolioIdentity</c> is internal to API.</para>
/// </summary>
public static class AttentionItemsBuilder
{
    /// <summary>
    /// The exclusion-filtered service list plus every count and flag <c>BuildOpsSummary</c>
    /// needs, so callers never have to recompute the filter themselves.
    /// </summary>
    public sealed record Result(
        List<string> AttentionItems,
        List<WebService> Services,
        int Total,
        int Active,
        int Broken,
        int SecurityFindings,
        int CleanupCandidates,
        bool IsStale);

    /// <param name="report">The scan result to summarize.</param>
    /// <param name="isExcluded">
    /// True when a service (by friendly name / resource name) must be excluded from health
    /// and attention accounting — this site itself and retired apps. Takes a
    /// <c>string?[]</c> (not two positional strings) so API's
    /// <c>PortfolioIdentity.IsSelf(params string?[])</c> converts straight to this
    /// delegate as a method group.
    /// </param>
    public static Result Build(AzureReport report, Func<string?[], bool> isExcluded)
    {
        // The dashboard never reports on the site rendering it, nor on retired apps. Both are
        // real resources in the scanned subscription, so they arrive through the inventory
        // path regardless of the catalog — this exclusion must be applied before any count or
        // attention item is derived from the service list.
        var services = (report.WebServices?.Services ?? new List<WebService>())
            .Where(s => !isExcluded([s.FriendlyName, s.Name]))
            .ToList();
        var total = services.Count;
        var active = services.Count(s => ServiceHealth.IsHealthy(s.HttpStatus));
        var broken = services.Count(s => ServiceHealth.IsBroken(s.HttpStatus));
        var cleanup = (report.OrphanedResources?.Count ?? 0)
            + (report.ZombieApps?.Count ?? 0)
            + report.AppServicePlanInventory.Count(p => p.AppCount == 0);
        var insecureStorage = (report.StorageInventory ?? new()).Count(s =>
            s.PublicBlobAccess || !s.HttpsOnly || s.MinTls is "TLS1_0" or "TLS1_1");
        var criticalDrift = (report.ConfigDrift ?? new()).Count(d =>
            d.Issues?.Any(i => SeverityLevel.Rank(i.Severity) <= SeverityLevel.Rank(SeverityLevel.High)) == true);
        var security = insecureStorage + criticalDrift;
        var attention = new List<string>();

        attention.AddRange(services
            .Where(s => !ServiceHealth.IsHealthy(s.HttpStatus))
            .Take(3)
            .Select(s => $"{(string.IsNullOrWhiteSpace(s.FriendlyName) ? s.Name : s.FriendlyName)} is unavailable"));
        if (security > 0)
            attention.Add($"{security} security configuration finding(s)");
        if (cleanup > 0)
            attention.Add($"{cleanup} cleanup candidate(s)");
        var isStale = PortfolioFreshness.IsStale(report.GeneratedAt, DateTime.UtcNow);
        if (isStale)
            attention.Add("Azure data is stale and should be refreshed");

        return new Result(attention, services, total, active, broken, security, cleanup, isStale);
    }
}
