using Microsoft.JSInterop;

namespace PoPunkouterSoftware.Client;

/// <summary>
/// Sonification: the ops summary rendered as sound rather than as pixels.
///
/// <para>This is a <em>presentation</em> aspect, exactly like
/// <c>AzureDashboard.Presentation.cs</c> — it maps an already-loaded <c>OpsSummary</c> onto
/// an output channel and adds no facts of its own. That is what keeps it inside the "each
/// fact appears once on /azure" rule: the health percentage, the outage count, the forecast
/// and the uptime grid are all already on screen; this plays them.</para>
///
/// <para>Everything crossing into JS here is a primitive — seven numbers — matching the
/// discipline every interop call in this app follows. No DTO is serialised, so nothing
/// new needs a <c>[JsonSerializable]</c> entry in <c>AppJsonContext</c> and the WASM trim
/// analyzer has nothing to complain about. The audio mapping itself lives in
/// js/audio-kit.js, which documents why each fact was given the parameter it was.</para>
/// </summary>
public partial class AzureDashboard
{
    /// <summary>
    /// Guards against a second overlapping playback. The composition is ~4.2 seconds and the
    /// voices are scheduled against the audio clock the moment the call is made, so two
    /// presses half a second apart would stack two full chords rather than restarting one.
    /// </summary>
    private bool _sonifying;

    private async Task SonifyAsync()
    {
        if (_summary is null || _sonifying)
            return;

        _sonifying = true;
        try
        {
            // The click is a real user gesture, which is the only moment a browser will let
            // an AudioContext start. Enabling here (rather than telling the visitor to go
            // find the header toggle first) is what makes one press produce one sound.
            await JS.InvokeVoidAsync("audioKit.setEnabled", true);

            // Uptime across the whole fleet, not one row: the per-row percentages are
            // per-scan observations, so averaging the rows is the same denominator the
            // heatmap itself reports. An empty grid is 100 — "nothing observed" is not
            // evidence of downtime, the same rule the pinger applies to a timeout.
            var uptime = _summary.Uptime.Rows.Count > 0
                ? _summary.Uptime.Rows.Average(r => r.UptimePercent)
                : 100d;

            // Projected spend as a fraction of budget. With no budget configured there is no
            // "over" to convey, so 1.0 (exactly on pace) keeps the tremolo neutral rather
            // than inventing urgency from a missing setting.
            var forecastRatio = _summary.Forecast.PercentOfBudget is int pct
                ? pct / 100d
                : 1d;

            await JS.InvokeVoidAsync("audioKit.sonifyOps",
                _summary.HealthPercent,
                _summary.BrokenServices,
                _summary.ActionableCount,
                _summary.SecurityFindings,
                _summary.CleanupCandidates,
                forecastRatio,
                uptime);

            // Hold the button disabled for the length of the composition. Not a timer for a
            // sound we cannot observe finishing — audio-kit.js schedules the whole gesture
            // up front, so its duration is known here and is the honest disable window.
            await Task.Delay(TimeSpan.FromSeconds(4.4));
        }
        catch (JSException)
        {
            // Decorative-adjacent: a browser with no Web Audio, or a blocked context, must
            // not surface as an error boundary on a status page.
        }
        catch (InvalidOperationException) { }
        catch (TaskCanceledException) { }
        finally
        {
            _sonifying = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Fire-and-forget SFX. Every call site is inside the refresh lifecycle, where an audio
    /// failure must never change what the scan does or how its outcome is reported — so this
    /// swallows everything and returns nothing.
    /// </summary>
    private async Task SfxAsync(string identifier, params object?[] args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, args);
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
        catch (TaskCanceledException) { }
    }
}
