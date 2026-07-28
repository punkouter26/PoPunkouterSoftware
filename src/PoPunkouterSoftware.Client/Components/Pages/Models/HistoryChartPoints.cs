namespace PoPunkouterSoftware.Client.Components.Pages.Models;

/// <summary>
/// One day of the 30-day fleet-status trend: how many services were active vs broken.
/// Projected from HistorySummary by AzureDashboard.Trends.
/// </summary>
public sealed record HistoryStatusPoint(string Label, double Active, double Broken);

/// <summary>
/// One day of the 30-day cost trend, in USD. Projected from HistorySummary by
/// AzureDashboard.Trends.
/// </summary>
/// <remarks>
/// Named TimePoint until 2026-07 — a name so generic it said nothing about what the
/// value measured, in a file that also had a StatusHistPoint sitting next to it.
/// </remarks>
public sealed record HistoryCostPoint(string Label, double Value);
