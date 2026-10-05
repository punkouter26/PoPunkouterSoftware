using Microsoft.JSInterop;
using PoPunkouterSoftware.Shared;
using System.Net.Http.Json;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// State and loading for the <c>/users</c> roster. The markup is in SignIns.razor and the
/// three blocks it composes are sibling components, following the same split AzureDashboard
/// uses.
/// </summary>
public partial class SignIns
{
    /// <summary>
    /// 90 is not a taste call: it is the retention on the shared Application Insights
    /// component, and <c>SignInQueryService.MaxWindowDays</c> clamps to it server-side. A
    /// fourth, longer choice would offer history that does not exist.
    /// </summary>
    private static readonly int[] WindowChoices = [7, 30, 90];

    private SignInReport? _report;
    private bool _loading = true;
    private string? _loadError;
    [Microsoft.AspNetCore.Components.SupplyParameterFromQuery(Name = "days")]
    public int? QueryDays { get; set; }

    private int _windowDays = 30;

    /// <summary>
    /// Built once per load and held, NOT computed in the markup. MetricBars guards its render
    /// on reference identity, so a fresh list per render would defeat the guard and redraw the
    /// ranking on every page render — the exact cost that component exists to remove.
    /// </summary>
    private List<OpsMetricPoint> _reachPoints = new();

    /// <summary>Cost per person, dearest first — held for the same reason as the reach points.</summary>
    private List<OpsMetricPoint> _costPoints = new();

    /// <summary>Comets play once per visit, on the first load — a window switch re-reads the
    /// same recent arrivals and must not replay them.</summary>
    private bool _cometsPlayed;

    protected override async Task OnInitializedAsync()
    {
        if (QueryDays.HasValue && WindowChoices.Contains(QueryDays.Value))
        {
            _windowDays = QueryDays.Value;
        }
        else
        {
            try
            {
                var settings = await SettingsService.GetSettingsAsync();
                if (WindowChoices.Contains(settings.DefaultSignInDays))
                {
                    _windowDays = settings.DefaultSignInDays;
                }
            }
            catch { }
        }

        await LoadAsync();
    }

    private async Task SetWindowAsync(int days)
    {
        if (days == _windowDays || _loading)
            return;

        _windowDays = days;
        try
        {
            NavManager.NavigateTo($"/users?days={days}", replace: true);
        }
        catch { }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading && _report is not null)
            return;

        _loading = true;
        _loadError = null;
        StateHasChanged();

        try
        {
            // Per-call budget. The shared HttpClient's timeout is sized for the huge report
            // endpoint; the server already caps this query at 20s and degrades rather than
            // hanging, so anything past 30s here is the network, not the query.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var loaded = await Http.GetFromJsonAsync(
                $"/api/signins?days={_windowDays}", AppJsonContext.Default.SignInReport, cts.Token);

            // Replace, never mutate: every ShouldRender guard on this page is reference
            // identity on these lists, and an in-place edit would be invisible to all of them.
            if (loaded is not null)
            {
                _report = loaded;
                _reachPoints = loaded.Apps
                    .Select(a => new OpsMetricPoint(a.App, a.People))
                    .ToList();
                _costPoints = loaded.Apps
                    .Where(a => a.CostPerPerson is not null)
                    .OrderByDescending(a => a.CostPerPerson)
                    .Select(a => new OpsMetricPoint(a.App, a.CostPerPerson!.Value))
                    .ToList();

                if (!_cometsPlayed)
                {
                    _cometsPlayed = true;
                    await PlayArrivalsAsync(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            // A failed reload leaves _report populated on purpose — see the banner comment in
            // the markup. Only a first load with nothing to fall back on renders empty.
            _loadError = ex.Message;
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// One comet across the GPU backdrop (and one soft pluck, if sound is on) per sign-in in
    /// the last 24 hours, capped at six by js/gpu-backdrop.js. 24h rather than 1h: at this
    /// estate's traffic an hour is nearly always empty, and "who showed up today" is the
    /// question the page opens on. Decorative, so every interop failure is swallowed.
    /// </summary>
    private async Task PlayArrivalsAsync(SignInReport loaded)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var arrivals = loaded.Recent.Count(e => e.Timestamp >= since);
        if (arrivals == 0)
            return;

        try
        {
            await JS.InvokeVoidAsync("appFx.comets", arrivals);
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }
}
