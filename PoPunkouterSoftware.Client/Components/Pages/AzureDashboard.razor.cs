using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Client.Components.Pages.Models;
using PoPunkouterSoftware.Shared.Azure;
using Radzen;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Client.Components.Pages;

public partial class AzureDashboard
{
    [Inject] private IWebAssemblyHostEnvironment? HostEnvironment { get; set; }

    private AzureReport? report;
    private List<WebService> services = new();
    private List<SafeToRemoveItem> safeToRemove = new();
    private int _selectedTabIndex = 0;
    private bool _loading = true;
    private string? _loadError;

    private List<ConsolidatedService> ConsolidatedServices => BuildConsolidatedServices(report);

    private IEnumerable<WebService> SortedServices =>
        services.OrderBy(s => s.HttpStatus == "active" ? 0 : 1).ThenBy(s => s.FriendlyName ?? s.Name);

    private List<PriorityQueueItem> PriorityQueue => BuildPriorityQueue(report, ConsolidatedServices, safeToRemove);

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
    private IDisposable? _locationChangingRegistration;
    private const int RefreshTimeoutSeconds = 120;
    private bool ShowAdvancedDiagnostics => string.Equals(HostEnvironment?.Environment, "Development", StringComparison.OrdinalIgnoreCase);

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
        if (report is null) return;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(report, options);
        var timestamp = (report.GeneratedAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd_HHmm");
        var filename = $"azure-report-{timestamp}.json";

        await JS.InvokeVoidAsync("downloadTextFile", filename, json, "application/json");
    }

    private void DownloadAutomationScript() =>
        NavManager.NavigateTo("/api/diag/automation-script", forceLoad: true);

    // ── SignalR hub connection ─────────────────────────────────────────────────
    private HubConnection? _hub;

    protected override async Task OnInitializedAsync()
    {
        _locationChangingRegistration = NavManager.RegisterLocationChangingHandler(OnLocationChanging);
        await LoadReportAsync();
        _ = LoadHistoryAsync();
    }

    private ValueTask OnLocationChanging(LocationChangingContext context)
    {
        if (_refreshing)
        {
            context.PreventNavigation();
            NotificationService.Notify(NotificationSeverity.Warning, "Scan in progress",
                "An Azure scan is running. Cancel it or wait for it to complete before leaving this page.");
        }
        return ValueTask.CompletedTask;
    }

    private async Task LoadReportAsync()
    {
        _loading = true;
        _loadError = null;
        StateHasChanged();

        try
        {
            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };

            report = await Http.GetFromJsonAsync<AzureReport>("/api/diag/report", opts);
            if (report is null)
                throw new InvalidOperationException("The Azure report endpoint returned no data.");

            services = report.WebServices?.Services ?? new List<WebService>();
            safeToRemove = BuildSafeToRemove(report);
        }
        catch (Exception ex)
        {
            report = null;
            services = new List<WebService>();
            safeToRemove = new List<SafeToRemoveItem>();
            _loadError = ex.Message;
            Console.Error.WriteLine($"Azure dashboard load error: {ex}");
        }
        finally
        {
            _loading = false;
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
        var initialGeneratedAt = report?.GeneratedAt;
        while (_refreshing && !ct.IsCancellationRequested)
        {
            await Task.Delay(delayMs, ct);
            if (!_refreshing)
                break;

            if (_hub is null || _hub.State != HubConnectionState.Connected)
            {
                try
                {
                    var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    var latest = await Http.GetFromJsonAsync<AzureReport>("/api/diag/report", opts, ct);
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

        _hub.On<object>("RefreshProgress", payload =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
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
        _locationChangingRegistration?.Dispose();
        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
