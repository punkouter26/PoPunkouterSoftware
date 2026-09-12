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
    private int _windowDays = 30;

    /// <summary>
    /// Built once per load and held, NOT computed in the markup. MetricBars guards its render
    /// on reference identity, so a fresh list per render would defeat the guard and redraw the
    /// ranking on every page render — the exact cost that component exists to remove.
    /// </summary>
    private List<OpsMetricPoint> _reachPoints = new();

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task SetWindowAsync(int days)
    {
        if (days == _windowDays || _loading)
            return;

        _windowDays = days;
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
}
