using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Domain.Azure;
using PoPunkouterSoftware.Shared.Azure;
using System.Collections.Concurrent;

namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Background service that periodically pings each Azure web service URL to warm cold-start instances.
/// Ping results are stored in IMemoryCache for the pinger-status endpoint.
/// </summary>
public sealed class ServicePingerService : BackgroundService
{
    private const string CacheKey = "pinger-status";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ServicePingerService> _logger;
    private readonly TimeSpan _interval;

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay 30 s on startup so the app fully initialises before the first sweep.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PingAllServicesAsync(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task PingAllServicesAsync(CancellationToken ct)
    {
        // Load the latest report to get the current service list.
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAzureReportRepository>();
        var reportResult = await repository.LoadAsync(ct);
        if (!reportResult.IsSuccess || reportResult.Value?.WebServices is null)
            return;

        var services = reportResult.Value.WebServices.Services;
        var client = _httpClientFactory.CreateClient("azure-probe");
        var results = new List<PingResult>();

        foreach (var svc in services)
        {
            if (string.IsNullOrWhiteSpace(svc.Url)) continue;
            if (_disabled.TryGetValue(svc.Name, out var dis) && dis) continue;

            var result = await PingOneAsync(client, svc.Name, svc.FriendlyName, svc.Url, ct);
            results.Add(result);
            _logger.LogDebug("Pinger: {Name} → {Status} ({Ms} ms)", svc.Name, result.Status, result.ResponseTimeMs);
        }

        _cache.Set(CacheKey, new PingerSnapshot(DateTime.UtcNow, results), TimeSpan.FromMinutes(30));
        _logger.LogInformation("Pinger sweep complete: {Total} services probed", results.Count);
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
