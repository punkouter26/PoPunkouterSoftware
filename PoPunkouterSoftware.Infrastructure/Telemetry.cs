using System.Diagnostics.Metrics;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Central OpenTelemetry <see cref="Meter"/> and instruments for the application's custom
/// metrics. The meter source is registered with the OpenTelemetry pipeline in Program.cs via
/// <c>AddMeter(Telemetry.MeterName)</c>, which exports it to Azure Monitor.
///
/// <para>Design rules (see docs/observability-audit.md):</para>
/// <list type="bullet">
///   <item>Counters answer "how often"; histograms answer "how slow"; gauges answer "what is it now".</item>
///   <item>Labels (tags) must come from small, fixed sets — status class, provider, bounded service
///   name. Never user IDs, raw URLs, or error-message text (cardinality bomb).</item>
/// </list>
/// </summary>
public static class Telemetry
{
    /// <summary>Meter source name — must match the AddMeter() registration in Program.cs.</summary>
    public const string MeterName = "PoPunkouterSoftware";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Maps an HTTP status code to a low-cardinality status class label (e.g. "2xx").</summary>
    public static string StatusClass(int statusCode) => $"{statusCode / 100}xx";

    // ─── Azure OpenAI (questions 7 & 8) ──────────────────────────────────────
    public static readonly Counter<long> AzureOpenAiCalls = Meter.CreateCounter<long>(
        "azure_openai_calls_total", unit: "{call}",
        description: "Azure OpenAI chat-completion calls, tagged by status_class and outcome.");

    public static readonly Histogram<double> AzureOpenAiDuration = Meter.CreateHistogram<double>(
        "azure_openai_call_duration", unit: "ms",
        description: "Azure OpenAI chat-completion call duration in milliseconds.");

    // ─── GitHub API (question 9) ─────────────────────────────────────────────
    public static readonly Counter<long> GitHubCalls = Meter.CreateCounter<long>(
        "github_api_calls_total", unit: "{call}",
        description: "GitHub API calls, tagged by status_class.");

    public static readonly Gauge<long> GitHubRateLimitRemaining = Meter.CreateGauge<long>(
        "github_rate_limit_remaining", unit: "{request}",
        description: "GitHub API requests remaining before rate limiting, from X-RateLimit-Remaining.");

    // ─── Service pinger (questions 4, 5 & 6) ─────────────────────────────────
    public static readonly Gauge<int> PingerServiceUp = Meter.CreateGauge<int>(
        "pinger_service_up", unit: "{service}",
        description: "1 if the monitored service responded with HTTP < 500, else 0. Tagged by service.");

    public static readonly Histogram<double> PingerResponseTime = Meter.CreateHistogram<double>(
        "pinger_response_time_ms", unit: "ms",
        description: "Monitored-service response time per ping. Tagged by service.");

    public static readonly Counter<long> PingerSweeps = Meter.CreateCounter<long>(
        "pinger_sweeps_total", unit: "{sweep}",
        description: "Completed pinger sweeps — a heartbeat; a flat rate means the loop has died.");

    // ─── Azure report refresh (questions 1, 2 & 3) ───────────────────────────
    public static readonly Counter<long> RefreshRuns = Meter.CreateCounter<long>(
        "azure_refresh_total", unit: "{run}",
        description: "Azure report refresh attempts, tagged by outcome (success|failed|cancelled|collision).");

    public static readonly Histogram<double> RefreshDuration = Meter.CreateHistogram<double>(
        "azure_refresh_duration", unit: "ms",
        description: "Azure report refresh wall-clock duration for completed runs.");
}
