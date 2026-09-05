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

    // ── Status narrative (see AzureStatusNarrative.razor) ───────────────────────
    // The persisted per-scan summary comes from whichever of the two read contracts is
    // freshest in view: the full report once advanced diagnostics has been opened, otherwise
    // the compact first-paint summary — both project the identical AzureReport.AiSummary
    // field, so this never disagrees with what the server most recently computed.
    private AiSummaryResult? CurrentAiSummary => report?.AiSummary ?? _summary?.AiSummary;

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

    // ── Snoozes ─────────────────────────────────────────────────────────────────
    // Findings have no server identity (see SnoozeStore / AGENT.MD "Snoozes"), so the
    // priority queue and cleanup list are filtered client-side against each item's own
    // dedup key — the same formats DerivedViews already groups on ({Source}|{Item} and
    // {Type}|{ResourceGroup}|{Name}), so a re-scan naturally re-matches a snooze to the
    // same finding without the server ever knowing what a "finding" is.
    private const int DefaultSnoozeDurationDays = 7;
    private List<SnoozeEntry> _activeSnoozes = new();
    private HashSet<string> _snoozedKeys = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildDerivedState()
    {
        _consolidatedServices = DashboardDerivations.BuildConsolidatedServices(report);
        // Post-filter rather than threading _snoozedKeys through the pure builder —
        // DashboardDerivations stays a pure function of the report, and this is the smaller change.
        _priorityQueue = DashboardDerivations.BuildPriorityQueue(report, _consolidatedServices, safeToRemove)
            .Where(i => !_snoozedKeys.Contains($"{i.Source}|{i.Item}"))
            .ToList();
        _resourceExplorerItems = DashboardDerivations.BuildResourceExplorerItems(report, _consolidatedServices, safeToRemove);
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

    // The live progress strip, addressed directly so a hub tick does not re-render the page.
    // See AzureRefreshProgress.razor and the RefreshProgress handler in EnsureHubConnectedAsync.
    private AzureRefreshProgress? _progressUi;

    private int _progressPercent;
    private string _progressStep = "";
    private bool _refreshFailed;
    private string? _refreshFailureMessage;
    private bool _userCancelled;
    // Captured at the start of RefreshAsync. The wait polls /api/diag/summary on the
    // HTTP fallback path; if the polling detects a newer report between the user's click
    // on Cancel and the next yield, the wait exits cleanly (no OperationCanceledException)
    // and the post-wait LoadSummaryAsync was free to overwrite `_summary` with the report
    // the user just said "don't bother me with". Tracking the baseline lets the post-wait
    // branch refuse that overwrite — a cancelled refresh leaves the page on the data the
    // user had before clicking, and the next scheduled or manual scan picks up the new one.
    private DateTime? _refreshBaselineGeneratedAt;
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

    // ── Snooze / un-snooze ────────────────────────────────────────────────────
    private async Task LoadSnoozesAsync()
    {
        try
        {
            var snoozes = await Http.GetFromJsonAsync("/api/diag/snoozes", AppJsonContext.Default.ListSnoozeEntry);
            _activeSnoozes = snoozes ?? new List<SnoozeEntry>();
        }
        catch
        {
            // Non-fatal: an empty/stale snooze list just means nothing is hidden — the
            // priority queue and cleanup list simply show everything, never a 500.
            _activeSnoozes = new List<SnoozeEntry>();
        }
        _snoozedKeys = _activeSnoozes.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SnoozeAsync(string key)
    {
        try
        {
            var payload = JsonSerializer.Serialize(
                new SnoozeRequest(key, DefaultSnoozeDurationDays, null), AppJsonContext.Default.SnoozeRequest);
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync("/api/diag/snooze", content);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await ReadProblemDetailAsync(resp);
                NotificationService.Notify(NotificationSeverity.Error, "Snooze failed",
                    detail ?? $"Could not snooze this item ({(int)resp.StatusCode}).");
                return;
            }

            await LoadSnoozesAsync();
            RebuildDerivedState();
            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Success,
                Summary = "Snoozed",
                Detail = $"Hidden for {DefaultSnoozeDurationDays} days — click to undo.",
                Duration = 6000,
                Click = msg => { _ = UnsnoozeAsync(key); },
            });
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Snooze failed", ex.Message);
        }
    }

    private Task SnoozePriorityItemAsync(PriorityQueueItem item) =>
        SnoozeAsync($"{item.Source}|{item.Item}");

    private Task SnoozeCleanupItemAsync(SafeToRemoveItem item) =>
        SnoozeAsync($"{item.Type}|{item.ResourceGroup}|{item.Name}");

    private async Task UnsnoozeAsync(string key)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new SnoozeRemoveRequest(key), AppJsonContext.Default.SnoozeRemoveRequest);
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            await Http.PostAsync("/api/diag/snooze/remove", content);
            await LoadSnoozesAsync();
            RebuildDerivedState();
            await InvokeAsync(StateHasChanged);
            NotificationService.Notify(NotificationSeverity.Success, "Un-snoozed", "The item is back in view.");
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Un-snooze failed", ex.Message);
        }
    }

    private Task UnsnoozeEntryAsync(SnoozeEntry entry) => UnsnoozeAsync(entry.Key);

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
            // Assign through a local so a failed parse cannot half-replace the live summary.
            var loaded = await Http.GetFromJsonAsync("/api/diag/summary", AppJsonContext.Default.OpsSummary)
                ?? throw new InvalidOperationException("The Azure status endpoint returned no data.");
            _summary = loaded;
        }
        catch (Exception ex)
        {
            // _summary is deliberately LEFT ALONE. This method runs on every rescan, so
            // nulling it meant one failed post-scan reload deleted the hero, all six panes and
            // the open advanced tree and replaced them with a banner — throwing away data still
            // held in memory. A reload that fails is a stale dashboard, not an empty one; the
            // markup renders the banner above whatever is still there. Same rule Index.razor
            // already follows for the catalog.
            _loadError = FriendlyError.Describe(ex);
            Console.Error.WriteLine($"Azure summary load error: {ex}");
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
            var loaded = await Http.GetFromJsonAsync("/api/diag/report", AppJsonContext.Default.AzureReport)
                ?? throw new InvalidOperationException("The Azure report endpoint returned no data.");
            report = loaded;

            services = loaded.WebServices?.Services ?? new List<WebService>();
            safeToRemove = DashboardDerivations.BuildSafeToRemove(loaded);
            // Snoozes can expire between visits, so this is refreshed on every report
            // load/refresh, not just once at startup.
            await LoadSnoozesAsync();
            RebuildDerivedState();
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            // The previously loaded report, its derived queue and its explorer rows are left
            // in place for the same reason LoadSummaryAsync leaves _summary alone: this runs
            // again after every rescan, and a failed reload that wipes the tree the visitor was
            // reading is strictly worse than one that leaves it standing under a banner.
            _advancedError = FriendlyError.Describe(ex);
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
        // Capture the timestamp the page is currently showing so the post-wait branch can
        // refuse an overwrite when the user cancelled — a new GeneratedAt after a Cancel
        // would be the report the scan wrote AFTER the user said "stop, don't bother me".
        _refreshBaselineGeneratedAt = _summary?.GeneratedAt ?? report?.GeneratedAt;
        // Keep the previous report in view during the scan — do not null it here.
        // The report will be replaced once the scan completes and LoadReportAsync is called again.
        _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(RefreshTimeoutSeconds));
        StateHasChanged();

        // A full ARM scan takes about thirty seconds, which is exactly the duration after
        // which someone switches tabs. The drone (and the chord that resolves it below) is
        // how the outcome reaches a visitor who is no longer looking at the page. Silent
        // unless they turned sound on — audioKit gates every one of these calls itself.
        await SfxAsync("audioKit.refreshStart");

        await EnsureHubConnectedAsync();

        try
        {
            var resp = await Http.PostAsync("/api/diag/refresh", null);
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                NotificationService.Notify(NotificationSeverity.Warning, "Already running", "A refresh is already in progress.");
                await SfxAsync("audioKit.refreshEnd", "cancelled");
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
                await SfxAsync("audioKit.refreshEnd", "failure");
                _refreshing = false;
                StateHasChanged();
                return;
            }

            if (_hub?.State == HubConnectionState.Connected)
            {
                await WaitForRefreshCompletionAsync(_refreshCts!.Token);
            }
            else
            {
                // No percent values will arrive, which is why the bar above renders
                // indeterminate. The drone deepens its tremolo to say the same thing rather
                // than sweeping its filter toward a completion it cannot actually observe.
                await SfxAsync("audioKit.refreshIndeterminate");
                await WaitForRefreshCompletionAsync(_refreshCts!.Token, delayMs: 1500);
            }

            // The user-cancelled race: when Cancel lands between the wait's last poll and
            // the next yield, the wait exits cleanly (no OperationCanceledException) and the
            // original code fell into LoadSummaryAsync, overwriting `_summary` with the new
            // report the scan wrote after the user said "stop". _userCancelled is set by
            // CancelRefreshAsync and is NOT reset by the wait, so checking it here honours
            // the user's intent regardless of which path the wait took to get out.
            if (!_userCancelled)
            {
                await LoadSummaryAsync();
                if (_advancedOpen)
                    await LoadReportAsync();
                if (_refreshFailed)
                {
                    NotificationService.Notify(NotificationSeverity.Error, "Refresh failed", _refreshFailureMessage ?? "Refresh failed. Check logs for details.");
                    await SfxAsync("audioKit.refreshEnd", "failure");
                }
                else if (!_refreshCts.Token.IsCancellationRequested)
                {
                    NotificationService.Notify(NotificationSeverity.Success, "Done", "Azure report refreshed successfully.");
                    await SfxAsync("audioKit.refreshEnd", "success");
                }
            }
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
            await SfxAsync("audioKit.refreshEnd", "timeout");
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Error", ex.Message);
            await SfxAsync("audioKit.refreshEnd", "failure");
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
            // Here rather than in the OperationCanceledException handler: that handler exists
            // precisely because the cancellation toast has already been shown, so the sound
            // belongs on the same side of that split as the toast it accompanies.
            await SfxAsync("audioKit.refreshEnd", "cancelled");
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
            var wasRefreshing = _refreshing;
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

            // Walk the drone's filter to match the bar. Fire-and-forget: a progress tick is
            // the hottest path on this page, and awaiting an interop call here would
            // serialise the hub callback behind the audio graph.
            _ = SfxAsync("audioKit.refreshProgress", _progressPercent);

            // Route the tick to the progress strip ALONE.
            //
            // This used to be a bare InvokeAsync(StateHasChanged) on the page. A scan emits
            // roughly twenty of these over thirty seconds, and each one re-rendered the hero,
            // four sparklines, both metric panels, the 30-day uptime grid and — whenever
            // Advanced diagnostics was open — the whole priority queue and the virtualized
            // resource explorer, on the single WASM thread, to move one bar. None of that
            // markup depends on a percentage.
            //
            // The page still renders on the transition that ENDS the scan (`done`), because
            // that is when _refreshing flips and the strip has to come out of the tree; and it
            // still renders if the strip has not been assigned yet, which is the window
            // between _refreshing going true and its first render completing.
            var terminal = wasRefreshing && !_refreshing;
            InvokeAsync(() =>
            {
                if (terminal || _progressUi is null)
                    StateHasChanged();
                else
                    _progressUi.Update(_progressPercent, _progressStep,
                        _hub?.State == HubConnectionState.Connected);
            });
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

        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
