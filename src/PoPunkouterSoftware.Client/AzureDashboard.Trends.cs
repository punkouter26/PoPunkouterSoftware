using PoPunkouterSoftware.Client;
using PoPunkouterSoftware.Shared;
using System.Net.Http.Json;

namespace PoPunkouterSoftware.Client;

// Partial class — Trends & History tab (merged from former DetailsPage).
// Uses the shared `report` field and `_history` loaded alongside the main report.
public partial class AzureDashboard
{
    private const int HistoryWindowDays = 30;

    // ── State ──────────────────────────────────────────────────────────────────
    private List<HistorySummary> _history = new();

    internal async Task LoadHistoryAsync()
    {
        try
        {
            var hist = await Http.GetFromJsonAsync("/api/diag/history", AppJsonContext.Default.ListHistorySummary);
            _history = hist ?? new();
        }
        catch
        {
            _history = new();
        }
        await InvokeAsync(StateHasChanged);
    }

    private List<HistorySummary> HistoryWindow
    {
        get
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-HistoryWindowDays);

            return _history
                .Where(h => h.GeneratedAt != DateTime.MinValue && h.GeneratedAt >= cutoffUtc)
                .GroupBy(h => h.GeneratedAt.ToLocalTime().Date)
                .Select(g => g.OrderByDescending(x => x.GeneratedAt).First())
                .OrderBy(h => h.GeneratedAt)
                .ToList();
        }
    }

    private static string ToChartDateLabel(DateTime utcDateTime) =>
        utcDateTime.ToLocalTime().ToString("MMM dd");

    private List<HistoryStatusPoint> ServiceStatusHistory =>
        HistoryWindow
            .Select(h => new HistoryStatusPoint(
                ToChartDateLabel(h.GeneratedAt),
                h.ActiveServices,
                h.BrokenServices))
            .ToList();

    private List<HistoryCostPoint> CostHistory =>
        HistoryWindow
            .Where(h => h.TotalCost30Days > 0)
            .Select(h => new HistoryCostPoint(
                ToChartDateLabel(h.GeneratedAt),
                Math.Round(h.TotalCost30Days, 2)))
            .ToList();

}
