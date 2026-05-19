using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Shared.Azure;
using Radzen;
using Radzen.Blazor;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Client.Components.Pages;

#pragma warning disable CS0414 // assigned but value never used — write-only loading state
public partial class AzureDashboard
{
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
    private CancellationTokenSource? _refreshCts;
    private IDisposable? _locationChangingRegistration;
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

    // ── Fix Plan state ────────────────────────────────────────────────────────
    private WebService? _fixPlanService;
    private string? _fixPlanText;
    private string? _fixPlanError;
    private string? _fixPlanDisabledMessage;
    private bool _fixPlanLoading;


    // ── Incidents state ───────────────────────────────────────────────────────
    private List<IncidentEntry> _incidents = new();
    private bool _incidentsLoading;

    // ── Resource type drill-down state ────────────────────────────────────────
    private string? _selectedResourceType;
    private List<ResourceDetail>? _selectedResourceDetails;

    // ── Service Status state ────────────────────────────────────────────────────
    private StatusPageReport? _statusReport;
    private bool _statusLoading;
    private string? _statusError;
    private bool _pingerLoaded;
    private DateTime? _pingerSweptAt;
    private Dictionary<string, PingResultDto> _pingerResults = new(StringComparer.OrdinalIgnoreCase);

    // ── Health check state ────────────────────────────────────────────────────
    private HealthCheckResult? _healthResult;
    private bool _healthLoading;
    private string? _healthError;

    // ── SignalR hub connection ─────────────────────────────────────────────────
    private HubConnection? _hub;

    protected override async Task OnInitializedAsync()
    {
        _locationChangingRegistration = NavManager.RegisterLocationChangingHandler(OnLocationChanging);
        await LoadReportAsync();
        _ = LoadIncidentsAsync();
        _ = LoadStatusAsync();
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
            if (!_refreshCts.Token.IsCancellationRequested)
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
        while (_refreshing && !ct.IsCancellationRequested)
        {
            await Task.Delay(delayMs, ct);
            if (!_refreshing)
                break;
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
                bool done = root.TryGetProperty("done", out var d) && d.GetBoolean();
                if (done)
                    _refreshing = false;
            }
            catch { }

            InvokeAsync(StateHasChanged);
        });

        await _hub.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _locationChangingRegistration?.Dispose();
        if (_hub is not null)
            await _hub.DisposeAsync();
    }

    private async Task OpenFixPlan(WebService service)
    {
        _fixPlanService = service;
        _fixPlanText = null;
        _fixPlanError = null;
        _fixPlanDisabledMessage = null;
        _fixPlanLoading = true;
        StateHasChanged();
        _selectedTabIndex = 2;

        try
        {
            var resp = await Http.GetAsync($"/api/diag/fix-plan/{Uri.EscapeDataString(service.Name)}");
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("disabled", out var dis) && dis.GetBoolean())
            {
                _fixPlanDisabledMessage = doc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() : "AI integration is disabled.";
            }
            else if (resp.IsSuccessStatusCode && doc.RootElement.TryGetProperty("plan", out var planEl))
            {
                _fixPlanText = planEl.GetString();
            }
            else
            {
                _fixPlanError = doc.RootElement.TryGetProperty("detail", out var det)
                    ? det.GetString()
                    : $"Request failed ({(int)resp.StatusCode}).";
            }
        }
        catch (Exception ex)
        {
            _fixPlanError = ex.Message;
        }
        finally
        {
            _fixPlanLoading = false;
            StateHasChanged();
        }
    }

    private async Task CopyToClipboard(string text)
    {
        try
        { await JS.InvokeVoidAsync("navigator.clipboard.writeText", text); }
        catch { }
        NotificationService.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Success,
            Summary = "Copied",
            Detail = "Command copied to clipboard",
            Duration = 2500
        });
    }

    private async Task RestartAppServiceAsync(string resourceGroup, string name)
    {
        try
        {
            var resp = await Http.PostAsync($"/api/manage/restart/{Uri.EscapeDataString(resourceGroup)}/{Uri.EscapeDataString(name)}", null);
            var msg = resp.IsSuccessStatusCode ? $"{name} restart initiated." : $"Restart failed ({(int)resp.StatusCode}).";
            var sev = resp.IsSuccessStatusCode ? NotificationSeverity.Success : NotificationSeverity.Error;
            NotificationService.Notify(sev, "App Service", msg);
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Restart failed", ex.Message);
        }
    }

    private async Task ScaleToFreeAsync(string resourceGroup, string planName)
    {
        try
        {
            var resp = await Http.PostAsync($"/api/manage/scale-free/{Uri.EscapeDataString(resourceGroup)}/{Uri.EscapeDataString(planName)}", null);
            var msg = resp.IsSuccessStatusCode ? $"{planName} scaled to Free tier." : $"Scale failed ({(int)resp.StatusCode}).";
            var sev = resp.IsSuccessStatusCode ? NotificationSeverity.Success : NotificationSeverity.Error;
            NotificationService.Notify(sev, "Scale", msg);
        }
        catch (Exception ex)
        {
            NotificationService.Notify(NotificationSeverity.Error, "Scale failed", ex.Message);
        }
    }

    private async Task LoadIncidentsAsync()
    {
        _incidentsLoading = true;
        StateHasChanged();
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = await Http.GetFromJsonAsync<List<IncidentEntry>>("/api/incidents?limit=50", opts);
            _incidents = result ?? new();
        }
        catch { _incidents = new(); }
        finally
        {
            _incidentsLoading = false;
            StateHasChanged();
        }
    }

    // ── Service Status ────────────────────────────────────────────────────────
    private async Task LoadStatusAsync()
    {
        _statusLoading = true;
        _statusError = null;
        StateHasChanged();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            _statusReport = await Http.GetFromJsonAsync<StatusPageReport>("/api/status", opts);
        }
        catch (Exception ex) { _statusError = ex.Message; }
        finally { _statusLoading = false; }

        // Load pinger results (non-fatal)
        try
        {
            var snap = await Http.GetFromJsonAsync<PingerSnapshotDto>("/api/pinger/status", opts);
            if (snap?.Swept == true && snap.Results is not null)
            {
                _pingerSweptAt = snap.SweptAt;
                _pingerResults = snap.Results.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
                _pingerLoaded = true;
            }
        }
        catch { /* non-fatal */ }
        StateHasChanged();
    }

    private static double CalcUptime(List<StatusSample> samples)
    {
        if (samples.Count == 0)
            return 100.0;
        var up = samples.Count(s => s.Status == "active");
        return (double)up / samples.Count * 100.0;
    }

    // ── Health check ──────────────────────────────────────────────────────────
    private async Task LoadHealthAsync()
    {
        _healthLoading = true;
        _healthError = null;
        StateHasChanged();
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _healthResult = await Http.GetFromJsonAsync<HealthCheckResult>("/api/health", opts);
        }
        catch (Exception ex) { _healthError = ex.Message; }
        finally
        {
            _healthLoading = false;
            StateHasChanged();
        }
    }

    private List<HealthCheckRow> GetHealthCheckRows() =>
        _healthResult?.Checks?.Select(kv =>
        {
            var detail = kv.Value.TryGetProperty("error", out var err) ? err.GetString() ?? ""
                : kv.Value.TryGetProperty("httpStatus", out var code)
                    ? kv.Value.TryGetProperty("note", out var noteEl) && noteEl.ValueKind != JsonValueKind.Null
                        ? $"HTTP {code} ({noteEl.GetString()})"
                        : $"HTTP {code}"
                    : "";
            var status = kv.Value.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
            return new HealthCheckRow(kv.Key, status, detail);
        }).ToList() ?? new();

    // ── Chart helpers ─────────────────────────────────────────────────────────

    private record HealthCheckResult(
        string Status,
        string Environment,
        DateTime Timestamp,
        Dictionary<string, JsonElement>? Checks,
        Dictionary<string, string>? Config);

    private record HealthCheckRow(string Name, string Status, string Detail);

    private record PingerSnapshotDto(
        [property: JsonPropertyName("swept")] bool Swept,
        [property: JsonPropertyName("sweptAt")] DateTime? SweptAt,
        [property: JsonPropertyName("results")] List<PingResultDto>? Results);

    private record PingResultDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("responseTimeMs")] long ResponseTimeMs,
        [property: JsonPropertyName("pingedAt")] DateTime PingedAt);
}
