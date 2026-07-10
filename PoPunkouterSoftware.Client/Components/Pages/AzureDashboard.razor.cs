using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Client.Components.Pages.Models;
using PoPunkouterSoftware.Shared.Azure;
using Radzen;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoPunkouterSoftware.Client.Components.Pages;

public partial class AzureDashboard
{
    private AzureReport? report;
    private OpsSummary? _summary;
    private List<WebService> services = new();
    private List<SafeToRemoveItem> safeToRemove = new();
    private bool _loading = true;
    private string? _loadError;
    private bool _advancedOpen;
    private bool _advancedLoading;
    private string? _advancedError;

    private List<ConsolidatedService> ConsolidatedServices => BuildConsolidatedServices(report);

    private IEnumerable<WebService> SortedServices =>
        services.OrderBy(s => s.HttpStatus == "active" ? 0 : 1).ThenBy(s => s.FriendlyName ?? s.Name);

    private List<PriorityQueueItem> PriorityQueue => BuildPriorityQueue(report, ConsolidatedServices, safeToRemove);

    private static readonly string[] ResourceViews = ["All", "Unhealthy", "Waste", "Security", "Drift"];
    private string _resourceView = "All";
    private List<ResourceExplorerItem> ResourceExplorerItems => BuildResourceExplorerItems(report, ConsolidatedServices, safeToRemove);
    private List<ResourceExplorerItem> FilteredResourceExplorerItems => ResourceExplorerItems
        .Where(item => _resourceView switch
        {
            "Unhealthy" => item.Risk.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase),
            "Waste" => item.Risk.Contains("Waste", StringComparison.OrdinalIgnoreCase),
            "Security" => item.Risk.Contains("Security", StringComparison.OrdinalIgnoreCase),
            "Drift" => item.Risk.Contains("Drift", StringComparison.OrdinalIgnoreCase),
            _ => true,
        })
        .ToList();

    private static string ReliabilityClass(int score) =>
        score < 70 ? "app-tone-danger" : score < 85 ? "app-tone-warning" : "app-tone-success";

    private static string ResponseTimeClass(int responseTime) =>
        responseTime < 1000 ? "app-tone-success" : responseTime < 3000 ? "app-tone-warning" : "app-tone-danger";

    private bool _refreshing;
    private int _progressPercent;
    private string _progressStep = "";
    private bool _refreshFailed;
    private string? _refreshFailureMessage;
    private CancellationTokenSource? _refreshCts;
    private const int RefreshTimeoutSeconds = 120;
    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1)
            return "just now";
        if (age.TotalMinutes < 60)
            return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24)
            return $"{(int)age.TotalHours}h {age.Minutes}m ago";
        return $"{(int)age.TotalDays}d {age.Hours}h ago";
    }

    private async Task DownloadReportAsync()
    {
        if (report is null)
            await LoadReportAsync();
        if (report is null) return;

        var json = JsonSerializer.Serialize(report, AppJsonContext.Default.AzureReport);
        var timestamp = (report.GeneratedAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd_HHmm");
        var filename = $"azure-report-{timestamp}.json";

        await JS.InvokeVoidAsync("downloadTextFile", filename, json, "application/json");
    }

    private void DownloadAutomationScript() =>
        NavManager.NavigateTo("/api/diag/automation-script", forceLoad: true);

    private async Task CopyText(string text)
    {
        await JS.InvokeVoidAsync("copyToClipboard", text);
        NotificationService.Notify(NotificationSeverity.Success, "Copied", "Azure CLI command copied to the clipboard.");
    }

    // ── SignalR hub connection ─────────────────────────────────────────────────
    private HubConnection? _hub;

    protected override async Task OnInitializedAsync()
    {
        await LoadSummaryAsync();
    }

    private async Task LoadSummaryAsync()
    {
        _loading = true;
        _loadError = null;
        try
        {
            _summary = await Http.GetFromJsonAsync("/api/diag/summary", AppJsonContext.Default.OpsSummary)
                ?? throw new InvalidOperationException("The Azure summary endpoint returned no data.");
        }
        catch (Exception ex)
        {
            _summary = null;
            _loadError = ex.Message;
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private async Task ToggleAdvancedAsync()
    {
        _advancedOpen = !_advancedOpen;
        if (_advancedOpen && report is null)
            await LoadReportAsync();
    }

    private async Task LoadReportAsync()
    {
        _advancedLoading = true;
        _advancedError = null;
        StateHasChanged();

        try
        {
            report = await Http.GetFromJsonAsync("/api/diag/report", AppJsonContext.Default.AzureReport);
            if (report is null)
                throw new InvalidOperationException("The Azure report endpoint returned no data.");

            services = report.WebServices?.Services ?? new List<WebService>();
            safeToRemove = BuildSafeToRemove(report);
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            report = null;
            services = new List<WebService>();
            safeToRemove = new List<SafeToRemoveItem>();
            _advancedError = ex.Message;
            Console.Error.WriteLine($"Azure dashboard load error: {ex}");
        }
        finally
        {
            _advancedLoading = false;
            StateHasChanged();
        }
    }

    private async Task RefreshAsync()
    {
        _refreshing = true;
        _refreshFailed = false;
        _refreshFailureMessage = null;
        _progressPercent = 0;
        _progressStep = "Starting…";
        // Keep the previous report in view during the scan — do not null it here.
        // The report will be replaced once the scan completes and LoadReportAsync is called again.
        _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(RefreshTimeoutSeconds));
        StateHasChanged();

        await EnsureHubConnectedAsync();

        try
        {
            var resp = await Http.PostAsync("/api/diag/refresh", null);
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                NotificationService.Notify(NotificationSeverity.Warning, "Already running", "A refresh is already in progress.");
                _refreshing = false;
                StateHasChanged();
                return;
            }

            if (_hub?.State == HubConnectionState.Connected)
                await WaitForRefreshCompletionAsync(_refreshCts!.Token);
            else
                await WaitForRefreshCompletionAsync(_refreshCts!.Token, delayMs: 1500);

            await LoadSummaryAsync();
            if (_advancedOpen)
                await LoadReportAsync();
            if (_refreshFailed)
                NotificationService.Notify(NotificationSeverity.Error, "Refresh failed", _refreshFailureMessage ?? "Refresh failed. Check logs for details.");
            else if (!_refreshCts.Token.IsCancellationRequested)
                NotificationService.Notify(NotificationSeverity.Success, "Done", "Azure report refreshed successfully.");
        }
        catch (OperationCanceledException)
        {
            NotificationService.Notify(NotificationSeverity.Warning, "Timeout", "Refresh took too long (120s limit). Partial results may be available.");
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Error", ex.Message);
        }
        finally
        {
            _refreshing = false;
            _refreshCts?.Dispose();
            _refreshCts = null;
            StateHasChanged();
        }
    }

    private async Task CancelRefreshAsync()
    {
        if (_refreshCts is not null)
        {
            _refreshCts.Cancel();
            NotificationService.Notify(NotificationSeverity.Warning, "Cancelled", "Refresh operation cancelled.");
        }
        // Signal the server to stop the in-progress scan (best-effort — swallow errors).
        try { await Http.PostAsync("/api/diag/cancel-refresh", null); } catch { }
        _refreshing = false;
        StateHasChanged();
    }

    private async Task WaitForRefreshCompletionAsync(CancellationToken ct, int delayMs = 2000)
    {
        var initialGeneratedAt = _summary?.GeneratedAt ?? report?.GeneratedAt;
        while (_refreshing && !ct.IsCancellationRequested)
        {
            await Task.Delay(delayMs, ct);
            if (!_refreshing)
                break;

            if (_hub is null || _hub.State != HubConnectionState.Connected)
            {
                try
                {
                    var latest = await Http.GetFromJsonAsync("/api/diag/summary", AppJsonContext.Default.OpsSummary, ct);
                    if (latest is not null && latest.GeneratedAt != initialGeneratedAt)
                    {
                        _refreshing = false;
                        _progressPercent = 100;
                        _progressStep = "Done";
                        break;
                    }
                }
                catch
                {
                    // Non-fatal transient HTTP error during polling
                }
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task EnsureHubConnectedAsync()
    {
        if (_hub is not null && _hub.State == HubConnectionState.Connected)
            return;
        if (_hub is not null)
        { await _hub.DisposeAsync(); }

        _hub = new HubConnectionBuilder()
            .WithUrl(NavManager.ToAbsoluteUri("/hubs/refresh"))
            .WithAutomaticReconnect()
            .Build();

        // Bind the hub payload straight to a JsonElement — no reflection-based
        // re-serialise round-trip, which keeps this trim-safe.
        _hub.On<JsonElement>("RefreshProgress", root =>
        {
            try
            {
                if (root.TryGetProperty("percent", out var pct))
                    _progressPercent = pct.GetInt32();
                if (root.TryGetProperty("step", out var step))
                    _progressStep = step.GetString() ?? "";
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                {
                    _refreshFailed = true;
                    _refreshFailureMessage = err.GetString();
                }
                bool done = root.TryGetProperty("done", out var d) && d.GetBoolean();
                if (done)
                    _refreshing = false;
            }
            catch { }

            InvokeAsync(StateHasChanged);
        });

        try
        {
            await _hub.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SignalR hub start connection error (refresh status updates will fall back to HTTP polling): {ex}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
