using Radzen;

namespace PoPunkouterSoftware.Client;

// Partial class — display-only mapping: raw domain strings to the labels, badge styles
// and CSS classes the markup renders. Nothing here reads or mutates page state, so a
// visual tweak never risks touching behaviour.
//
// (Named .Charts.cs until 2026-07, by which point it contained no chart code at all —
// every chart-data property in it had been orphaned by the compact-dashboard redesign.)
public partial class AzureDashboard
{
    private long StepTimingTotalMs => report?.StepTimings?.Sum(x => x.ElapsedMs) ?? 0;

    private static BadgeStyle ActionabilityBadge(string? tier) => tier switch
    {
        "Fix Now" => BadgeStyle.Danger,
        "Fix Soon" => BadgeStyle.Warning,
        "Remove Candidate" => BadgeStyle.Secondary,
        _ => BadgeStyle.Info,
    };

    private static BadgeStyle ExplorerStatusBadge(string status) => status switch
    {
        "active" or "healthy" => BadgeStyle.Success,
        "broken" => BadgeStyle.Danger,
        "unreachable" or "review" => BadgeStyle.Warning,
        _ => BadgeStyle.Info,
    };

    /// <summary>Maps a cleanup-candidate's confidence (SafeToRemoveItem.Confidence) to its
    /// badge colour, mirroring ActionabilityBadge/ExplorerStatusBadge above — used by the
    /// shared SeverityBadge component in AzureEvidenceDisclosures' cleanup-evidence list.</summary>
    private static BadgeStyle ConfidenceBadge(string? confidence) => confidence?.ToLowerInvariant() switch
    {
        "high" => BadgeStyle.Warning,
        "medium" => BadgeStyle.Info,
        "low" => BadgeStyle.Secondary,
        _ => BadgeStyle.Secondary,
    };

    private static string RiskClass(string risk) => risk switch
    {
        var value when value.Contains("Security", StringComparison.OrdinalIgnoreCase) => "danger",
        var value when value.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) => "danger",
        var value when value.Contains("Waste", StringComparison.OrdinalIgnoreCase) => "warning",
        var value when value.Contains("Drift", StringComparison.OrdinalIgnoreCase) => "warning",
        _ => "muted",
    };
}
