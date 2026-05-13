using Microsoft.AspNetCore.Components;
using PoPunkouterSoftware.Shared.Azure;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoPunkouterSoftware.Client.Components.Pages;

public partial class DetailsPage
{
    [Inject] private HttpClient Http { get; set; } = default!;

    private AzureReport? _report;
    private List<HistorySummary> _history = new();
    private bool _loading = true;
    private string? _error;

    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    protected override async Task OnInitializedAsync()
    {
        try
        {
            AzureReport? report = null;
            List<HistorySummary>? history = null;

            var reportTask = Http.GetFromJsonAsync<AzureReport>("/api/diag/report", _opts)
                .ContinueWith(t => { if (t.IsCompletedSuccessfully) report = t.Result; });
            var histTask = Http.GetFromJsonAsync<List<HistorySummary>>("/api/diag/history", _opts)
                .ContinueWith(t => { if (t.IsCompletedSuccessfully) history = t.Result; });

            await Task.WhenAll(reportTask, histTask);
            _report = report;
            _history = history ?? new();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Summary helpers ───────────────────────────────────────────────────────

    private int ActiveCount => _report?.WebServices?.ByStatus?.Active ?? 0;
    private int BrokenCount => _report?.WebServices?.ByStatus?.Broken ?? 0;
    private int TotalCount => _report?.WebServices?.Total ?? 0;
    private double Cost30d => _report?.Cost?.TotalCost30Days ?? 0;
    private int OrphanedCount => _report?.OrphanedResources?.Count ?? 0;
    private int SslCritical => _report?.SslExpiry?.Count(e => e.DaysLeft is not null && e.DaysLeft <= 30) ?? 0;

    private string ScanAge => _report?.GeneratedAt is DateTime dt
        ? $"{(int)(DateTime.UtcNow - dt).TotalMinutes}m ago"
        : "unknown";

    // ── Chart records ─────────────────────────────────────────────────────────

    private record TimePoint(string Label, double Value);
    private record StatusHistPoint(string Label, double Active, double Broken);
    private record ServiceTimePoint(string Label, double ResponseMs);

    // ── History charts ────────────────────────────────────────────────────────

    private bool HasHistory => _history.Count > 1;

    private List<StatusHistPoint> ServiceStatusHistory =>
        _history
            .Where(h => h.GeneratedAt != DateTime.MinValue)
            .Select(h => new StatusHistPoint(
                h.GeneratedAt.ToLocalTime().ToString("MM/dd HH:mm"),
                h.ActiveServices,
                h.BrokenServices))
            .ToList();

    private List<TimePoint> CostHistory =>
        _history
            .Where(h => h.GeneratedAt != DateTime.MinValue && h.TotalCost30Days > 0)
            .Select(h => new TimePoint(
                h.GeneratedAt.ToLocalTime().ToString("MM/dd HH:mm"),
                Math.Round(h.TotalCost30Days, 2)))
            .ToList();

    private List<TimePoint> AvgResponseHistory =>
        _history
            .Where(h => h.GeneratedAt != DateTime.MinValue && h.AvgResponseTimeMs > 0)
            .Select(h => new TimePoint(
                h.GeneratedAt.ToLocalTime().ToString("MM/dd HH:mm"),
                Math.Round(h.AvgResponseTimeMs, 0)))
            .ToList();

    private List<string> TrackedServiceNames =>
        _history
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
        _history
            .Where(h => h.GeneratedAt != DateTime.MinValue)
            .Select(h => new ServiceTimePoint(
                h.GeneratedAt.ToLocalTime().ToString("MM/dd HH:mm"),
                h.Services.FirstOrDefault(s => s.Name == name)?.ResponseTimeMs ?? 0))
            .Where(p => p.ResponseMs > 0)
            .ToList();

    // ── Current report helpers ────────────────────────────────────────────────

    private IEnumerable<WebService> AllServices =>
        (_report?.WebServices?.Services ?? new())
            .OrderBy(s => s.HttpStatus == "active" ? 0 : s.HttpStatus == "broken" ? 1 : 2)
            .ThenBy(s => s.FriendlyName ?? s.Name);

    private static string HttpStatusBadgeStyle(string status) => status switch
    {
        "active" => "background:var(--rz-success);color:#fff",
        "broken" => "background:var(--rz-danger);color:#fff",
        "unreachable" => "background:var(--rz-warning);color:#fff",
        _ => "background:var(--rz-base-300);color:var(--rz-text-color)",
    };

    private static string SslBadgeStyle(SslEntry e) => (e.DaysLeft ?? 999) switch
    {
        <= 0 => "background:var(--rz-danger);color:#fff",
        <= 14 => "background:var(--rz-danger);color:#fff",
        <= 30 => "background:var(--rz-warning);color:#fff",
        _ => "background:var(--rz-success);color:#fff",
    };

    private static string SslLabel(SslEntry e) => e.DaysLeft switch
    {
        null => "unknown",
        <= 0 => "EXPIRED",
        <= 14 => $"{e.DaysLeft}d CRITICAL",
        <= 30 => $"{e.DaysLeft}d WARNING",
        _ => $"{e.DaysLeft}d OK",
    };

    private static string SeverityStyle(string sev) => sev.ToLowerInvariant() switch
    {
        "critical" or "high" => "color:var(--rz-danger)",
        "medium" or "warning" => "color:var(--rz-warning)",
        _ => "color:var(--rz-text-disabled-color)",
    };
}
