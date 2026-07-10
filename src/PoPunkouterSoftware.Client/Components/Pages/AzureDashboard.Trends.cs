using PoPunkouterSoftware.Shared.Azure;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoPunkouterSoftware.Client.Components.Pages;

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

    // ── Summary helpers ────────────────────────────────────────────────────────
    private int TotalCount => report?.WebServices?.Total ?? 0;

    private string ScanAge => report?.GeneratedAt is DateTime dt
        ? FormatAge(DateTime.UtcNow - dt)
        : "unknown";

    // ── Chart records ──────────────────────────────────────────────────────────
    private record TimePoint(string Label, double Value);
    private record TrafficPoint(string Service, int Requests, int Http5xx);
    private record StatusHistPoint(string Label, double Active, double Broken);
    private record ServiceTimePoint(string Label, double ResponseMs);
    private record DeltaPoint(string Label, int Delta);

    // ── Traffic data ───────────────────────────────────────────────────────────
    private List<TrafficPoint> WebTrafficByService =>
        (report?.WebServices?.Services ?? new())
            .Where(s => s.Metrics7Days?.Requests > 0)
            .OrderByDescending(s => s.Metrics7Days!.Requests)
            .Select(s => new TrafficPoint(
                s.FriendlyName ?? s.Name,
                s.Metrics7Days!.Requests,
                s.Metrics7Days!.Http5xx))
            .ToList();

    // ── History chart data ─────────────────────────────────────────────────────
    private bool HasHistory => HistoryWindow.Count > 1;

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

    private List<StatusHistPoint> ServiceStatusHistory =>
        HistoryWindow
            .Select(h => new StatusHistPoint(
                ToChartDateLabel(h.GeneratedAt),
                h.ActiveServices,
                h.BrokenServices))
            .ToList();

    private List<TimePoint> CostHistory =>
        HistoryWindow
            .Where(h => h.TotalCost30Days > 0)
            .Select(h => new TimePoint(
                ToChartDateLabel(h.GeneratedAt),
                Math.Round(h.TotalCost30Days, 2)))
            .ToList();

    private List<TimePoint> AvgResponseHistory =>
        HistoryWindow
            .Where(h => h.AvgResponseTimeMs > 0)
            .Select(h => new TimePoint(
                ToChartDateLabel(h.GeneratedAt),
                Math.Round(h.AvgResponseTimeMs, 0)))
            .ToList();

    private List<string> TrackedServiceNames =>
        HistoryWindow
            .SelectMany(h => h.Services
                .Where(s => s.HttpStatus == "active")
                .Select(s => s.Name))
            .GroupBy(n => n)
            .OrderByDescending(g => g.Count())
            .Take(6)
            .Select(g => g.Key)
            .OrderBy(n => n)
            .ToList();

    private List<ServiceTimePoint> GetServiceResponseHistory(string name) =>
        HistoryWindow
            .Select(h => new ServiceTimePoint(
                ToChartDateLabel(h.GeneratedAt),
                h.Services.FirstOrDefault(s => s.Name == name)?.ResponseTimeMs ?? 0))
            .Where(p => p.ResponseMs > 0)
            .ToList();

    private List<TimePoint> Errors5xxHistory =>
        HistoryWindow
            .Select(h => new TimePoint(
                ToChartDateLabel(h.GeneratedAt),
                h.Total5xxErrors))
            .ToList();

    private List<TimePoint> UptimePctHistory =>
        HistoryWindow
            .Where(h => h.TotalServices > 0)
            .Select(h => new TimePoint(
                ToChartDateLabel(h.GeneratedAt),
                Math.Round(h.ActiveServices / (double)h.TotalServices * 100, 1)))
            .ToList();

    private List<DeltaPoint> BrokenDeltaHistory =>
        HistoryWindow
            .Where(h => h.BrokenDelta.HasValue)
            .Select(h => new DeltaPoint(
                ToChartDateLabel(h.GeneratedAt),
                h.BrokenDelta!.Value))
            .ToList();

    private List<TimePoint> ResourceCountHistory =>
        HistoryWindow
            .Where(h => h.TotalResources > 0)
            .Select(h => new TimePoint(
                ToChartDateLabel(h.GeneratedAt),
                h.TotalResources))
            .ToList();

    private List<TimePoint> ScanDurationHistory =>
        HistoryWindow
            .Where(h => h.ScanDurationMs > 0)
            .Select(h => new TimePoint(
                ToChartDateLabel(h.GeneratedAt),
                (double)h.ScanDurationMs))
            .ToList();

    // ── All-services grid ──────────────────────────────────────────────────────
    private IEnumerable<WebService> AllServices =>
        (report?.WebServices?.Services ?? new())
            .OrderBy(s => s.HttpStatus == "active" ? 0 : s.HttpStatus == "broken" ? 1 : 2)
            .ThenBy(s => s.FriendlyName ?? s.Name);

    // ── Badge / style helpers (inline-style variants used in Trends tab) ────────
    private static string HttpStatusBadgeStyle(string status) => status switch
    {
        "active"      => "background:var(--rz-success);color:#fff",
        "broken"      => "background:var(--rz-danger);color:#fff",
        "unreachable" => "background:var(--rz-warning);color:#fff",
        _             => "background:var(--rz-base-300);color:var(--rz-text-color)",
    };

    private static string SslBadgeStyle(SslEntry e) => (e.DaysLeft ?? 999) switch
    {
        <= 0  => "background:var(--rz-danger);color:#fff",
        <= 14 => "background:var(--rz-danger);color:#fff",
        <= 30 => "background:var(--rz-warning);color:#fff",
        _     => "background:var(--rz-success);color:#fff",
    };

    private static string SslLabel(SslEntry e) => e.DaysLeft switch
    {
        null  => "unknown",
        <= 0  => "EXPIRED",
        <= 14 => $"{e.DaysLeft}d CRITICAL",
        <= 30 => $"{e.DaysLeft}d WARNING",
        _     => $"{e.DaysLeft}d OK",
    };

    private static string SeverityStyle(string sev) => sev.ToLowerInvariant() switch
    {
        "critical" or "high"     => "color:var(--rz-danger)",
        "medium" or "warning"    => "color:var(--rz-warning)",
        _                        => "color:var(--rz-text-disabled-color)",
    };
}
