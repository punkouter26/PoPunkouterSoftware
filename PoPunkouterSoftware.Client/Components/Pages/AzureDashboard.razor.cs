using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using PoPunkouterSoftware.Shared.Azure;
using Radzen;
using Radzen.Blazor;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
#pragma warning disable CS0414 // fields assigned by background methods; display removed from UI

namespace PoPunkouterSoftware.Client.Components.Pages;

public partial class AzureDashboard
{
    private AzureReport? report;
    private List<WebService> services = new();
    private List<SafeToRemoveItem> safeToRemove = new();
    private int _selectedTabIndex = 0;

    private async Task ScrollTabsIntoView()
    {
        await JS.InvokeVoidAsync("eval",
            "document.getElementById('detail-tabs')?.scrollIntoView({behavior:'smooth',block:'start'})");
    }

    private bool _loading = true;
    private string? _loadError;

    private List<ConsolidatedService> ConsolidatedServices => BuildConsolidatedServices(report);

    private IEnumerable<WebService> SortedServices =>
        services.OrderBy(s => s.HttpStatus == "active" ? 0 : 1).ThenBy(s => s.FriendlyName ?? s.Name);

    private List<PortfolioRollup> PortfolioByEnvironment =>
        ConsolidatedServices
            .GroupBy(s => s.Environment)
            .Select(g => new PortfolioRollup(g.Key, g.Count(), g.Count(x => x.HttpStatus != "active"), Math.Round(g.Average(x => x.HealthScore), 1)))
            .OrderByDescending(r => r.Apps)
            .ToList();

    private List<PortfolioRollup> PortfolioByPlatform =>
        ConsolidatedServices
            .GroupBy(s => s.ResourceTypeSummary)
            .Select(g => new PortfolioRollup(g.Key, g.Count(), g.Count(x => x.HttpStatus != "active"), Math.Round(g.Average(x => x.HealthScore), 1)))
            .OrderByDescending(r => r.Apps)
            .ToList();

    private List<PriorityQueueItem> PriorityQueue => BuildPriorityQueue(report, ConsolidatedServices, safeToRemove);

    private static string ReliabilityClass(int score) =>
        score < 70 ? "app-tone-danger" : score < 85 ? "app-tone-warning" : "app-tone-success";

    private static string ResponseTimeClass(int responseTime) =>
        responseTime < 1000 ? "app-tone-success" : responseTime < 3000 ? "app-tone-warning" : "app-tone-danger";

    private bool _refreshing;
    private int _progressPercent;
    private string _progressStep = "";
    private CancellationTokenSource? _refreshCts;
    private const int RefreshTimeoutSeconds = 120;

    private string LastRefreshTime => report?.GeneratedAt is DateTime dt
        ? $" · Last updated {(DateTime.UtcNow - dt).TotalMinutes:F0}m ago"
        : "";

    // ── Fix Plan state ────────────────────────────────────────────────────────
    private WebService? _fixPlanService;
    private string? _fixPlanText;
    private string? _fixPlanError;
    private string? _fixPlanDisabledMessage;
    private bool _fixPlanLoading;

    // ── CI/CD Review state ────────────────────────────────────────────────────
    private List<InfraReview> _infraReviews = new();
    private bool _infraLoading;
    private string? _infraError;
    private string? _infraDisabledMessage;
    private InfraReview? _infraSelected;

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
        await LoadReportAsync();
        _ = LoadIncidentsAsync();
        _ = LoadStatusAsync();
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

            if (report.GeneratedAt is DateTime gen && (DateTime.UtcNow - gen).TotalHours > 2 && !_refreshing)
                _ = RefreshAsync();
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
        report = null;
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
                await WaitForRefreshCompletionWithFallbackAsync(_refreshCts!.Token);

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

    private Task CancelRefreshAsync()
    {
        if (_refreshCts is not null)
        {
            _refreshCts.Cancel();
            NotificationService.Notify(NotificationSeverity.Warning, "Cancelled", "Refresh operation cancelled.");
        }
        _refreshing = false;
        StateHasChanged();
        return Task.CompletedTask;
    }

    private async Task WaitForRefreshCompletionAsync(CancellationToken ct)
    {
        while (_refreshing && !ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            if (!_refreshing)
                break;
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task WaitForRefreshCompletionWithFallbackAsync(CancellationToken ct)
    {
        while (_refreshing && !ct.IsCancellationRequested)
        {
            await Task.Delay(1500, ct);
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
        if (_hub is not null)
            await _hub.DisposeAsync();
    }

    private static List<SafeToRemoveItem> BuildSafeToRemove(AzureReport? r)
    {
        if (r == null)
            return new();
        var items = new List<SafeToRemoveItem>();
        foreach (var svc in r.WebServices?.Services ?? new())
        {
            var zero = svc.Metrics7Days?.Requests == 0;
            var broken = svc.HttpStatus == "broken";
            var unreachable = svc.HttpStatus == "unreachable";
            var stopped = svc.PlatformState == "Stopped";
            var azErr = svc.Connectivity?.IsAzureErrorPage == true;
            if ((broken || unreachable || stopped || azErr) && zero)
            {
                var reasons = new List<string>();
                if (broken)
                    reasons.Add("HTTP broken");
                if (unreachable)
                    reasons.Add("Unreachable (timeout)");
                if (stopped)
                    reasons.Add("Platform Stopped");
                if (azErr)
                    reasons.Add("Serving Azure error page");
                if (zero)
                    reasons.Add("0 requests in 7 days");
                items.Add(new SafeToRemoveItem
                {
                    Name = svc.Name,
                    Source = "Connectivity + Metrics",
                    Reason = string.Join(", ", reasons),
                    Confidence = broken ? "high" : "medium",
                    Command = svc.ResourceType == "Microsoft.Web/sites"
                        ? $"az webapp delete --name \"{svc.Name}\" --resource-group \"{svc.ResourceGroup}\""
                        : null,
                });
            }
        }
        items.Sort((a, b) =>
        {
            var o = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
            return o.GetValueOrDefault(a.Confidence) - o.GetValueOrDefault(b.Confidence);
        });
        return items;
    }

    private static string TypeLabel(string? t) => t switch
    {
        "Microsoft.Web/sites" => "App Service",
        "Microsoft.App/containerApps" => "Container App",
        "Microsoft.Web/staticSites" => "Static Web App",
        _ => t?.Split('/').LastOrDefault() ?? "—",
    };

    private static BadgeStyle HttpBadgeStyle(string? s) => s switch
    {
        "active" => BadgeStyle.Success,
        "broken" => BadgeStyle.Danger,
        _ => BadgeStyle.Warning,
    };

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
            var resp = await Http.PostAsync($"/api/diag/fix-plan/{Uri.EscapeDataString(service.Name)}", null);
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

    private List<ChartPoint> FleetHealthDonutData
    {
        get
        {
            var ws = report?.WebServices;
            if (ws == null)
                return new();
            return new List<ChartPoint>
            {
                new("Active", ws.ByStatus?.Active ?? 0),
                new("Broken", ws.ByStatus?.Broken ?? 0),
                new("Other",  ws.ByStatus?.Other  ?? 0),
            }.Where(p => p.Value > 0).ToList();
        }
    }

    private List<ChartPoint> CostDriversBarData =>
        report?.Cost?.TopCostDrivers
            .Where(d => d.Cost > 0)
            .Take(10)
            .Select(d => new ChartPoint(
                d.Name.Length > 28 ? d.Name[..25] + "…" : d.Name,
                Math.Round(d.Cost, 2)))
            .ToList() ?? new();

    private List<ChartPoint> ResponseTimeBarData =>
        services
            .Where(s => s.Connectivity?.ResponseTime > 0)
            .OrderByDescending(s => s.Connectivity!.ResponseTime)
            .Take(15)
            .Select(s =>
            {
                var lbl = s.FriendlyName ?? s.Name;
                return new ChartPoint(lbl.Length > 20 ? lbl[..17] + "…" : lbl, s.Connectivity!.ResponseTime);
            })
            .ToList();

    private List<ChartPoint> ErrorRateBarData =>
        services
            .Where(s => s.Metrics7Days?.Http5xx > 0)
            .OrderByDescending(s => s.Metrics7Days!.Http5xx)
            .Take(15)
            .Select(s =>
            {
                var lbl = s.FriendlyName ?? s.Name;
                return new ChartPoint(lbl.Length > 20 ? lbl[..17] + "…" : lbl, s.Metrics7Days!.Http5xx);
            })
            .ToList();

    private void SelectResourceType(string typeLabel)
    {
        if (_selectedResourceType == typeLabel)
        {
            _selectedResourceType = null;
            _selectedResourceDetails = null;
            return;
        }
        _selectedResourceType = typeLabel;
        _selectedResourceDetails = report?.AllResourceSummary?.ResourcesByType.GetValueOrDefault(typeLabel);
    }

    private record ChartPoint(string Label, double Value);
    private record DailyCostChartPoint(string Date, double Cost);

    private List<ChartPoint> ResourceTypeChartData =>
        report?.AllResourceSummary?.ByType
            .OrderByDescending(kv => kv.Value).Take(10)
            .Select(kv => new ChartPoint(kv.Key, kv.Value)).ToList() ?? new();

    private List<ChartPoint> ResourceTypeTableData =>
        report?.AllResourceSummary?.ByType
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ChartPoint(kv.Key, kv.Value)).ToList() ?? new();

    private List<ChartPoint> CostByRgData
    {
        get
        {
            if (report?.Cost?.TopCostDrivers is null)
                return new();
            var byRg = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in report.Cost.TopCostDrivers)
            {
                var m = System.Text.RegularExpressions.Regex.Match(d.Name, @"\(([^)]+)\)$");
                var rg = m.Success ? m.Groups[1].Value : "Other";
                byRg[rg] = byRg.GetValueOrDefault(rg) + d.Cost;
            }
            return byRg.OrderByDescending(kv => kv.Value).Take(12)
                .Select(kv => new ChartPoint(kv.Key, Math.Round(kv.Value, 2))).ToList();
        }
    }

    private List<DailyCostChartPoint> BurnRateChartData =>
        report?.BurnRate?.DailyCosts
            .Select(d => new DailyCostChartPoint(
                DateTime.TryParse(d.Date, out var dt) ? dt.ToString("MMM d") : d.Date,
                d.Cost)).ToList() ?? new();

    private long StepTimingTotalMs => report?.StepTimings?.Sum(x => x.ElapsedMs) ?? 0;
    private StepTimingEntry? SlowestStepTiming => report?.StepTimings?.OrderByDescending(x => x.ElapsedMs).FirstOrDefault();

    private int BurnRateLabelStep =>
        BurnRateChartData.Count > 20 ? 5 :
        BurnRateChartData.Count > 10 ? 3 : 1;

    // ── CI/CD Review helpers ──────────────────────────────────────────────────
    private async Task LoadInfraReviewAsync()
    {
        _infraLoading = true;
        _infraError = null;
        _infraDisabledMessage = null;
        StateHasChanged();
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var resp = await Http.GetAsync("/api/infra/cicd-review");
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("disabled", out var dis) && dis.GetBoolean())
            {
                _infraDisabledMessage = doc.RootElement.TryGetProperty("message", out var msg)
                    ? msg.GetString() : "GitHub PAT is not configured.";
                _infraReviews = new();
            }
            else if (doc.RootElement.TryGetProperty("reviews", out var rev))
            {
                _infraReviews = rev.Deserialize<List<InfraReview>>(opts) ?? new();
            }
        }
        catch (Exception ex)
        {
            _infraError = ex.Message;
        }
        finally
        {
            _infraLoading = false;
            StateHasChanged();
        }
    }

    private async Task RescanInfraAsync()
    {
        await Http.PostAsync("/api/infra/cicd-review/refresh", null);
        _infraReviews = new();
        _infraSelected = null;
        await LoadInfraReviewAsync();
    }

    private void ToggleInfraDetail(InfraReview review)
    {
        _infraSelected = _infraSelected?.RepoName == review.RepoName ? null : review;
    }

    private static BadgeStyle TargetBadge(string? target) => target switch
    {
        "Container Apps" => BadgeStyle.Info,
        "App Service" => BadgeStyle.Primary,
        "Static Web Apps" => BadgeStyle.Success,
        "Azure Functions" => BadgeStyle.Warning,
        "AKS" => BadgeStyle.Danger,
        "Container Instance" => BadgeStyle.Light,
        "ARM/Bicep Deploy" => BadgeStyle.Secondary,
        _ => BadgeStyle.Light,
    };

    private static BadgeStyle TriggerBadge(string trigger) => trigger switch
    {
        "push" => BadgeStyle.Primary,
        "pull_request" => BadgeStyle.Success,
        "workflow_dispatch" => BadgeStyle.Warning,
        "schedule" => BadgeStyle.Info,
        "release" => BadgeStyle.Danger,
        _ => BadgeStyle.Light,
    };

    private static BadgeStyle InfraFileBadge(string fileType) => fileType switch
    {
        "bicep" => BadgeStyle.Primary,
        "arm" => BadgeStyle.Info,
        "azd" => BadgeStyle.Success,
        "docker" => BadgeStyle.Warning,
        "compose" => BadgeStyle.Warning,
        _ => BadgeStyle.Light,
    };

    private static BadgeStyle ActionabilityBadge(string? tier) => tier switch
    {
        "Fix Now" => BadgeStyle.Danger,
        "Fix Soon" => BadgeStyle.Warning,
        "Remove Candidate" => BadgeStyle.Secondary,
        _ => BadgeStyle.Info,
    };

    private static List<ConsolidatedService> BuildConsolidatedServices(AzureReport? r)
    {
        if (r is null)
            return new();
        var sourceServices = r.WebServices?.Services ?? new List<WebService>();
        if (sourceServices.Count == 0)
            return new();

        var grouped = sourceServices
            .GroupBy(s => CanonicalAppKey(s.FriendlyName, s.Name))
            .ToList();

        var raw = new List<ConsolidatedService>();
        foreach (var group in grouped)
        {
            var entries = group.ToList();
            var first = entries[0];
            var requests7d = entries.Sum(x => x.Metrics7Days?.Requests ?? 0);
            var http5xx7d = entries.Sum(x => x.Metrics7Days?.Http5xx ?? 0);
            var hasBroken = entries.Any(x => x.HttpStatus is "broken" or "unreachable" || x.Connectivity?.IsAzureErrorPage == true || x.PlatformState == "Stopped");
            var rtCandidates = entries.Where(x => x.Connectivity?.Success == true).Select(x => x.Connectivity!.ResponseTime).ToList();
            var responseMs = rtCandidates.Count > 0 ? (int?)Math.Round(rtCandidates.Average()) : null;
            var status = hasBroken ? "broken" : entries.Any(x => x.HttpStatus == "active") ? "active" : "other";

            var reliability = 100;
            reliability -= hasBroken ? 35 : 0;
            reliability -= Math.Min(30, http5xx7d * 3);
            reliability -= responseMs is > 3000 ? 20 : responseMs is > 1200 ? 10 : 0;
            reliability -= requests7d == 0 ? 10 : 0;
            reliability = Math.Clamp(reliability, 0, 100);

            var health = reliability;
            if (entries.Any(x => x.FreeTierCheck?.CanGoFree == true))
                health -= 5;
            health = Math.Clamp(health, 0, 100);

            raw.Add(new ConsolidatedService(
                Key: group.Key,
                DisplayName: string.IsNullOrWhiteSpace(first.FriendlyName) ? first.Name : first.FriendlyName,
                ResourceTypeSummary: string.Join(" + ", entries.Select(x => TypeLabel(x.ResourceType)).Distinct()),
                HttpStatus: status,
                Requests7d: requests7d,
                Http5xx7d: http5xx7d,
                ResponseTimeMs: responseMs,
                HealthScore: health,
                ReliabilityScore: reliability,
                Actionability: ToActionability(status, http5xx7d, requests7d),
                HasAnomaly: false,
                Owner: InferOwner(first.ResourceGroup, first.Name),
                Environment: InferEnvironment(first.ResourceGroup, first.Name),
                Criticality: InferCriticality(first.ResourceGroup, first.Name),
                ResourceGroup: first.ResourceGroup,
                Command: first.ResourceType == "Microsoft.Web/sites"
                    ? $"az webapp show --name \"{first.Name}\" --resource-group \"{first.ResourceGroup}\""
                    : null
            ));
        }

        var medianReq = Median(raw.Select(x => x.Requests7d).ToList());
        var medianRt = Median(raw.Where(x => x.ResponseTimeMs.HasValue).Select(x => (double)x.ResponseTimeMs!.Value).ToList());

        return raw
            .Select(x => x with { HasAnomaly = IsAnomalous(x, medianReq, medianRt) })
            .OrderBy(x => x.HealthScore)
            .ThenBy(x => x.DisplayName)
            .ToList();
    }

    private static List<PriorityQueueItem> BuildPriorityQueue(AzureReport? r, List<ConsolidatedService> consolidated, List<SafeToRemoveItem> safe)
    {
        var items = new List<PriorityQueueItem>();

        foreach (var c in consolidated.Where(x => x.Actionability is "Fix Now" or "Fix Soon"))
        {
            items.Add(new PriorityQueueItem(
                Actionability: c.Actionability,
                Item: c.DisplayName,
                Source: "Reliability",
                ImpactScore: 100 - c.HealthScore,
                Confidence: c.HealthScore < 50 ? "high" : "medium",
                Reason: $"Status={c.HttpStatus}; 7d 5xx={c.Http5xx7d}; Reliability={c.ReliabilityScore}%",
                Owner: c.Owner,
                Environment: c.Environment,
                Command: c.Command
            ));
        }

        foreach (var s in safe)
        {
            items.Add(new PriorityQueueItem(
                Actionability: "Remove Candidate",
                Item: s.Name,
                Source: "SafeToRemove",
                ImpactScore: s.Confidence == "high" ? 75 : 55,
                Confidence: s.Confidence,
                Reason: s.Reason,
                Owner: "unassigned",
                Environment: "unknown",
                Command: s.Command
            ));
        }

        foreach (var ssl in r?.SslExpiry?.Where(x => x.DaysLeft is < 60).Take(20) ?? Enumerable.Empty<SslEntry>())
        {
            var days = ssl.DaysLeft ?? 0;
            items.Add(new PriorityQueueItem(
                Actionability: days < 14 ? "Fix Now" : "Fix Soon",
                Item: ssl.Name,
                Source: "SSL",
                ImpactScore: days < 14 ? 90 : 65,
                Confidence: "high",
                Reason: $"Certificate expires in {days} days",
                Owner: "unassigned",
                Environment: InferEnvironment(ssl.Name, ssl.Name),
                Command: null
            ));
        }

        foreach (var orphan in r?.OrphanedResources ?? new List<OrphanedResource>())
        {
            items.Add(new PriorityQueueItem(
                Actionability: "Remove Candidate",
                Item: orphan.Name,
                Source: "Orphaned",
                ImpactScore: 60,
                Confidence: "medium",
                Reason: orphan.Reason,
                Owner: "unassigned",
                Environment: InferEnvironment(orphan.ResourceGroup, orphan.Name),
                Command: orphan.Command
            ));
        }

        return items
            .OrderByDescending(i => i.ImpactScore)
            .ThenBy(i => i.Actionability)
            .Take(150)
            .ToList();
    }

    private static bool IsAnomalous(ConsolidatedService service, double medianReq, double medianRt)
    {
        var requestAnomaly = medianReq > 0 && service.Requests7d > medianReq * 3;
        var responseAnomaly = service.ResponseTimeMs.HasValue && medianRt > 0 && service.ResponseTimeMs.Value > medianRt * 2;
        var errorAnomaly = service.Http5xx7d > 5;
        return requestAnomaly || responseAnomaly || errorAnomaly;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var ordered = values.OrderBy(v => v).ToList();
        var mid = ordered.Count / 2;
        return ordered.Count % 2 == 0 ? (ordered[mid - 1] + ordered[mid]) / 2 : ordered[mid];
    }

    private static double Median(List<int> values) => Median(values.Select(v => (double)v).ToList());

    private static string CanonicalAppKey(string? friendly, string? name)
    {
        var source = string.IsNullOrWhiteSpace(friendly) ? (name ?? "unknown") : friendly;
        return source.Trim().ToLowerInvariant();
    }

    private static string InferEnvironment(string? resourceGroup, string? name)
    {
        var text = $"{resourceGroup} {name}".ToLowerInvariant();
        if (text.Contains("prod") || text.Contains("production"))
            return "prod";
        if (text.Contains("dev") || text.Contains("test") || text.Contains("staging"))
            return "dev";
        return "shared";
    }

    private static string InferOwner(string? resourceGroup, string? name)
    {
        var token = (resourceGroup ?? name ?? "")
            .Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? "unassigned" : token.ToLowerInvariant();
    }

    private static string InferCriticality(string? resourceGroup, string? name)
    {
        var text = $"{resourceGroup} {name}".ToLowerInvariant();
        if (text.Contains("prod") || text.Contains("core") || text.Contains("api"))
            return "high";
        if (text.Contains("dev") || text.Contains("test"))
            return "low";
        return "medium";
    }

    private static string ToActionability(string status, int http5xx7d, int req7d)
    {
        if (status == "broken" || http5xx7d > 10)
            return "Fix Now";
        if (http5xx7d > 0 || (status != "active" && req7d > 0))
            return "Fix Soon";
        if (status != "active" && req7d == 0)
            return "Remove Candidate";
        return "Watch";
    }

    // ── Private records ───────────────────────────────────────────────────────
    private record ConsolidatedService(
        string Key,
        string DisplayName,
        string ResourceTypeSummary,
        string HttpStatus,
        int Requests7d,
        int Http5xx7d,
        int? ResponseTimeMs,
        int HealthScore,
        int ReliabilityScore,
        string Actionability,
        bool HasAnomaly,
        string Owner,
        string Environment,
        string Criticality,
        string? ResourceGroup,
        string? Command
    );

    private record PriorityQueueItem(
        string Actionability,
        string Item,
        string Source,
        int ImpactScore,
        string Confidence,
        string Reason,
        string Owner,
        string Environment,
        string? Command
    );

    private record PortfolioRollup(string Dimension, int Apps, int Broken, double AvgHealth);

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
