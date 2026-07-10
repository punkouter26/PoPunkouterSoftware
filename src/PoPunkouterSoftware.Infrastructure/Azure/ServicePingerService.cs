using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared.Azure;
using System.Collections.Concurrent;

namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Background service that periodically pings each Azure web service URL to warm cold-start instances.
/// Ping results are stored in IMemoryCache for the pinger-status endpoint.
/// </summary>
public sealed partial class ServicePingerService : BackgroundService
{
    private const string CacheKey = "pinger-status";

    // Source-generated logging — zero boxing/allocation on the per-ping hot path.
    [LoggerMessage(Level = LogLevel.Information, Message = "Pinger disabled by configuration.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pinger: {Name} -> {Status} ({Ms} ms)")]
    private partial void LogPing(string name, string status, long ms);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pinger sweep complete: {Total} services probed")]
    private partial void LogSweepComplete(int total);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ServicePingerService> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;
    private readonly int _maxConcurrency;

    // Per-service opt-out: populated by PingerEndpoints.ToggleService.
    // ConcurrentDictionary so the endpoint can write from a different thread safely.
    private readonly ConcurrentDictionary<string, bool> _disabled = new(StringComparer.OrdinalIgnoreCase);

    public ServicePingerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<ServicePingerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _interval = TimeSpan.FromMinutes(config.GetValue<int>("Pinger:IntervalMinutes", 10));
        _enabled = config.GetValue("Pinger:Enabled", true);
        _maxConcurrency = Math.Clamp(config.GetValue("Pinger:MaxConcurrency", 4), 1, 12);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            LogDisabled();
            return;
        }

        try
        {
            // Delay 30 s on startup so the app fully initialises before the first sweep.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await PingAllServicesAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal graceful shutdown — host cancellation token fired. Do not rethrow.
        }
    }

    private async Task PingAllServicesAsync(CancellationToken ct)
    {
        // Load the latest report to get the current service list.
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<AzureReportStore>();
        var reportResult = await repository.LoadAsync(ct);
        if (!reportResult.IsSuccess || reportResult.Value?.WebServices is null)
            return;

        var services = reportResult.Value.WebServices.Services;
        var client = _httpClientFactory.CreateClient("azure-probe");
        var results = new List<PingResult>();

        using var gate = new SemaphoreSlim(_maxConcurrency);
        var tasks = services
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .Where(s => !_disabled.TryGetValue(s.Name, out var dis) || !dis)
            .Select(async svc =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await PingOneAsync(client, svc.Name, svc.FriendlyName, svc.Url!, ct);
                LogPing(svc.Name, result.Status, result.ResponseTimeMs);

                // Metrics: per-service up/down gauge + response-time histogram so reachability
                // and latency trends are alertable without parsing logs. (questions 4 & 6)
                var serviceTag = new KeyValuePair<string, object?>("service", svc.Name);
                Telemetry.PingerServiceUp.Record(result.Status == "reachable" ? 1 : 0, serviceTag);
                Telemetry.PingerResponseTime.Record(result.ResponseTimeMs, serviceTag);
                return result;
            }
            finally
            {
                gate.Release();
            }
        });

        results.AddRange(await Task.WhenAll(tasks));

        _cache.Set(CacheKey, new PingerSnapshot(DateTime.UtcNow, results), TimeSpan.FromMinutes(30));
        // Heartbeat: a flat sweep-counter rate means the background loop has silently died. (question 5)
        Telemetry.PingerSweeps.Add(1);
        LogSweepComplete(results.Count);
    }

    private static async Task<PingResult> PingOneAsync(
        HttpClient client, string name, string friendlyName, string url, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(14));
            var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            var status = (int)resp.StatusCode < 500 ? "reachable" : "degraded";
            return new PingResult(name, friendlyName, url, status, sw.ElapsedMilliseconds, null, DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new PingResult(name, friendlyName, url, "timeout", sw.ElapsedMilliseconds, "Request timed out", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PingResult(name, friendlyName, url, "unreachable", sw.ElapsedMilliseconds, ex.Message, DateTime.UtcNow);
        }
    }

    /// <summary>Called by PingerEndpoints to enable/disable a service without restarting.</summary>
    public void SetDisabled(string serviceName, bool disabled) =>
        _disabled[serviceName] = disabled;

    public bool IsDisabled(string serviceName) =>
        _disabled.TryGetValue(serviceName, out var v) && v;

    public PingerSnapshot? CurrentSnapshot() =>
        _cache.Get<PingerSnapshot>(CacheKey);
}

public sealed record PingResult(
    string Name,
    string FriendlyName,
    string Url,
    string Status,
    long ResponseTimeMs,
    string? Error,
    DateTime PingedAt);

public sealed record PingerSnapshot(DateTime SweptAt, List<PingResult> Results);
