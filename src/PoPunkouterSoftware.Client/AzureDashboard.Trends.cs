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

    // Memoized once per history load — HistoryWindow/ServiceStatusHistory/CostHistory used to
    // be expression-bodied properties that re-ran their GroupBy/OrderBy pipeline on every
    // access, and the markup reads all three in the same render (see the RebuildDerivedState
    // comment in AzureDashboard.razor.cs for the same fix applied to the report-derived views).
    private List<HistorySummary> _historyWindow = new();
    private List<HistoryStatusPoint> _serviceStatusHistory = new();
    private List<HistoryCostPoint> _costHistory = new();

    private List<HistorySummary> HistoryWindow => _historyWindow;
    private List<HistoryStatusPoint> ServiceStatusHistory => _serviceStatusHistory;
    private List<HistoryCostPoint> CostHistory => _costHistory;

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
        RebuildHistoryDerivedState();
        await InvokeAsync(StateHasChanged);
    }

    private void RebuildHistoryDerivedState()
    {
        var cutoffUtc = DateTime.UtcNow.AddDays(-HistoryWindowDays);

        _historyWindow = _history
            .Where(h => h.GeneratedAt != DateTime.MinValue && h.GeneratedAt >= cutoffUtc)
            .GroupBy(h => h.GeneratedAt.ToLocalTime().Date)
            .Select(g => g.OrderByDescending(x => x.GeneratedAt).First())
            .OrderBy(h => h.GeneratedAt)
            .ToList();

        _serviceStatusHistory = _historyWindow
            .Select(h => new HistoryStatusPoint(
                ToChartDateLabel(h.GeneratedAt),
                h.ActiveServices,
                h.BrokenServices))
            .ToList();

        _costHistory = _historyWindow
            .Where(h => h.TotalCost30Days > 0)
            .Select(h => new HistoryCostPoint(
                ToChartDateLabel(h.GeneratedAt),
                Math.Round(h.TotalCost30Days, 2)))
            .ToList();
    }

    private static string ToChartDateLabel(DateTime utcDateTime) =>
        utcDateTime.ToLocalTime().ToString("MMM dd");
}
