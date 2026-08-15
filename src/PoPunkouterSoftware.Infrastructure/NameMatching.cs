namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Cross-cutting fuzzy-name-matching helper, shared by the services that correlate one
/// Azure/GitHub name against another (App Service ↔ GitHub repo, App Service ↔ App Insights
/// component) so the matching rule is defined once instead of drifting between call sites.
/// </summary>
public static class NameMatching
{
    /// <summary>True when either string contains the other, case-insensitively.</summary>
    public static bool FuzzyContains(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
}
