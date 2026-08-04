using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Client;
using PoPunkouterSoftware.Shared;
using Radzen;
using Radzen.Blazor;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoPunkouterSoftware.Client;

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

    // ── Memoized analysis model ────────────────────────────────────────────────
    // These were previously expression-bodied properties that re-ran the full
    // GroupBy/median/sort pipeline on EVERY property access — and the markup reads
    // them several times per render, with a render per SignalR progress tick. On the
    // single WASM thread that meant ~10 full model rebuilds per progress update.
    // They are now computed exactly once per report load (RebuildDerivedState) and
    // once per filter change (ApplyResourceFilter).
    private List<ConsolidatedService> _consolidatedServices = new();
    private List<PriorityQueueItem> _priorityQueue = new();
    private List<ResourceExplorerItem> _resourceExplorerItems = new();
    private List<ResourceExplorerItem> _filteredResourceExplorerItems = new();

    private List<PriorityQueueItem> PriorityQueue => _priorityQueue;
    private List<ResourceExplorerItem> ResourceExplorerItems => _resourceExplorerItems;
    private List<ResourceExplorerItem> FilteredResourceExplorerItems => _filteredResourceExplorerItems;

    private static readonly string[] ResourceViews = ["All", "Unhealthy", "Waste", "Security", "Drift"];
    private string _resourceView = "All";

    // ── Responsive branch ──────────────────────────────────────────────────────
    // The resource explorer renders as a virtualized grid on wide viewports and as a
    // card list on narrow ones. Previously BOTH were emitted and one was hidden with
    // `display: none`, so every row was built into the DOM twice. This mirrors the
    // 720px breakpoint in AzureDashboard.razor.css — keep the two in sync.
    private const string NarrowQuery = "(max-width: 720px)";
    private bool _isNarrow;
    private int _mediaSubscriptionId;
    private DotNetObjectReference<AzureDashboard>? _selfRef;

    [JSInvokable]
    public Task OnNarrowChanged(bool isNarrow)
    {
        if (_isNarrow == isNarrow)
            return Task.CompletedTask;

        _isNarrow = isNarrow;
        return InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
            return;

        _selfRef = DotNetObjectReference.Create(this);
        // appMedia.register fires the callback once with the current match before it
        // returns, so _isNarrow is correct by the time the explorer is first opened.
        _mediaSubscriptionId = await JS.InvokeAsync<int>(
            "appMedia.register", NarrowQuery, _selfRef, nameof(OnNarrowChanged));
    }

    private void RebuildDerivedState()
    {
        _consolidatedServices = BuildConsolidatedServices(report);
        _priorityQueue = BuildPriorityQueue(report, _consolidatedServices, safeToRemove);
        _resourceExplorerItems = BuildResourceExplorerItems(report, _consolidatedServices, safeToRemove);
        ApplyResourceFilter();
    }

    private void SetResourceView(string view)
    {
        _resourceView = view;
        ApplyResourceFilter();
    }

    private void ApplyResourceFilter() =>
        _filteredResourceExplorerItems = _resourceExplorerItems
            .Where(item => _resourceView switch
            {
                "Unhealthy" => item.Risk.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase),
                "Waste" => item.Risk.Contains("Waste", StringComparison.OrdinalIgnoreCase),
                "Security" => item.Risk.Contains("Security", StringComparison.OrdinalIgnoreCase),
                "Drift" => item.Risk.Contains("Drift", StringComparison.OrdinalIgnoreCase),
                _ => true,
            })
            .ToList();

    private bool _refreshing;
    private int _progressPercent;
    private string _progressStep = "";
    private bool _refreshFailed;
    private string? _refreshFailureMessage;
    private bool _userCancelled;
    private CancellationTokenSource? _refreshCts;
    private const int RefreshTimeoutSeconds = 120;

    // Whether the server allows management actions (POST /refresh etc.). In Production the
    // ManagementActionFilter rejects them with 403 unless explicitly enabled — showing an
    // active Refresh button that can only ever fail is a lie, so it is hidden instead.
    private bool _managementActionsEnabled = true;

    private async Task DownloadReportAsync()
    {
        if (report is null)
            await LoadReportAsync();
        if (report is null)
            return;

        var json = JsonSerializer.Serialize(report, AppJsonContext.Default.AzureReport);
        var timestamp = (report.GeneratedAt ?? DateTime.UtcNow).ToString("yyyy-MM-dd_HHmm");
        var filename = $"azure-report-{timestamp}.json";

        await JS.InvokeVoidAsync("downloadTextFile", filename, json, "application/json");
    }

    private void DownloadAutomationScript() =>
        NavManager.NavigateTo("/api/diag/automation-script", forceLoad: true);

    /// <summary>
    /// RadzenSplitButton passes <c>null</c> when the primary (left) half is pressed, and the
    /// selected <see cref="RadzenSplitButtonItem"/> when a menu entry is chosen. The primary
    /// half performs the common action — downloading the full JSON report.
    /// </summary>
    private async Task OnDownloadClick(RadzenSplitButtonItem? item)
    {
        if (item?.Value == "script")
            DownloadAutomationScript();
        else
            await DownloadReportAsync();
    }

    private async Task CopyText(string text)
    {
        bool copied;
        try
        {
            copied = await JS.InvokeAsync<bool>("copyToClipboard", text);
        }
        catch (JSException)
        {
            copied = false;
        }

        if (copied)
            NotificationService.Notify(NotificationSeverity.Success, "Copied", "Azure CLI command copied to the clipboard.");
        else
            NotificationService.Notify(NotificationSeverity.Error, "Copy failed", "The clipboard is unavailable — copy the command manually.");
    }

    // ── SignalR hub connection ─────────────────────────────────────────────────
    private HubConnection? _hub;

    protected override async Task OnInitializedAsync()
    {
        await LoadConfigAsync();
        await LoadSummaryAsync();
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cfg = await Http.GetFromJsonAsync("/api/config", AppJsonContext.Default.ConfigResponse, cts.Token);
            _managementActionsEnabled = cfg?.ManagementActionsEnabled ?? false;
        }
        catch
        {
            // Non-fatal: keep the button visible; a rejected POST is still handled gracefully.
        }
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
            RebuildDerivedState();
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            report = null;
            services = new List<WebService>();
            safeToRemove = new List<SafeToRemoveItem>();
            RebuildDerivedState();
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
        _userCancelled = false;
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

            if (!resp.IsSuccessStatusCode)
            {
                // 403 (management actions disabled), 401 (bad key), 5xx — previously only 409
                // was handled, so these fell into the polling loop and were misreported as a
                // 120-second "timeout" every single time.
                var detail = await ReadProblemDetailAsync(resp);
                NotificationService.Notify(NotificationSeverity.Error, "Refresh rejected",
                    detail ?? $"The server rejected the refresh request ({(int)resp.StatusCode}).");
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
        catch (OperationCanceledException) when (_userCancelled)
        {
            // The user clicked Cancel — the cancellation toast was already shown; a second,
            // contradictory "Timeout" toast here would tell them it timed out instead.
        }
        catch (OperationCanceledException)
        {
            // Client-side timeout. The server scan would otherwise keep running (and keep the
            // refresh lock) for up to 10 minutes — tell it to stop instead of walking away.
            try
            { await Http.PostAsync("/api/diag/cancel-refresh", null); }
            catch { }
            NotificationService.Notify(NotificationSeverity.Warning, "Timeout",
                "Refresh took too long (120s limit); the server scan was cancelled.");
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

    private static async Task<string?> ReadProblemDetailAsync(HttpResponseMessage resp)
    {
        try
        {
            var problem = JsonSerializer.Deserialize(
                await resp.Content.ReadAsStringAsync(), AppJsonContext.Default.ProblemResponse);
            return string.IsNullOrWhiteSpace(problem?.Detail) ? null : problem.Detail;
        }
        catch
        {
            return null;
        }
    }

    private async Task CancelRefreshAsync()
    {
        if (_refreshCts is not null)
        {
            _userCancelled = true;
            _refreshCts.Cancel();
            NotificationService.Notify(NotificationSeverity.Warning, "Cancelled", "Refresh operation cancelled.");
        }
        // Signal the server to stop the in-progress scan (best-effort — swallow errors).
        try
        { await Http.PostAsync("/api/diag/cancel-refresh", null); }
        catch { }
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
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;

        if (_mediaSubscriptionId != 0)
        {
            // Best-effort: the circuit may already be gone during teardown.
            try
            { await JS.InvokeVoidAsync("appMedia.unregister", _mediaSubscriptionId); }
            catch (JSException) { }
            catch (InvalidOperationException) { }
            catch (TaskCanceledException) { }
        }
        _selfRef?.Dispose();

        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
