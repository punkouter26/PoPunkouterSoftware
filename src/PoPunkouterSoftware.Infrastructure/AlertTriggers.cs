using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// The scan-to-scan comparisons that decide whether <c>ReportRefreshRunner</c> pushes an
/// alert. Every rule fires on a CROSSING, never on a state: a certificate that is 10 days from
/// expiry tonight was 11 days from it yesterday, and nagging nightly about the same fact is how
/// a notification channel gets muted. Pure and I/O-free.
/// </summary>
public static class AlertTriggers
{
    /// <summary>Days of certificate life left at which an expiry becomes worth a push.</summary>
    public const int SslAlertDays = 14;

    /// <summary>
    /// Certificates that are inside <see cref="SslAlertDays"/> now and were not in the previous
    /// report — either further out, or absent (a newly added host already close to expiry).
    /// </summary>
    public static List<SslEntry> NewSslExpiries(AzureReport current, AzureReport? previous)
    {
        var before = (previous?.SslExpiry ?? [])
            .Where(s => s.DaysLeft is <= SslAlertDays)
            .Select(s => s.Url ?? s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (current.SslExpiry ?? [])
            .Where(s => s.DaysLeft is <= SslAlertDays && !before.Contains(s.Url ?? s.Name))
            .OrderBy(s => s.DaysLeft)
            .ToList();
    }

    /// <summary>
    /// The current scan's forecast when it has just crossed into "over budget"; null when it
    /// has not, when the previous scan was already over, or when no budget is set.
    /// </summary>
    public static CostForecast? BudgetCrossed(AzureReport current, AzureReport? previous, double? budgetUsd)
    {
        var now = DashboardInsightsBuilder.BuildForecast(current, [], budgetUsd);
        if (now.Verdict != CostVerdict.Over)
            return null;
        return previous is not null && DashboardInsightsBuilder.BuildForecast(previous, [], budgetUsd).Verdict == CostVerdict.Over
            ? null
            : now;
    }
}
