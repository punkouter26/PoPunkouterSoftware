using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Detects service health transitions after each report refresh and stores them in Table Storage.
/// Optionally POSTs to a configured webhook URL on each new incident.
/// </summary>
public sealed class IncidentService(
    AzureReportStore repository,
    IConfiguration config,
    IHttpClientFactory httpFactory,
    ILogger<IncidentService> logger)
{
    private readonly AzureReportStore _repository = repository;
    private readonly IConfiguration _config = config;
    private readonly IHttpClientFactory _httpFactory = httpFactory;
    private readonly ILogger<IncidentService> _logger = logger;

    // Cached after first successful init — this is a singleton, so re-walking the
    // DefaultAzureCredential chain and calling CreateIfNotExists per refresh is pure waste.
    private TableClient? _cachedClient;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    /// <summary>
    /// Compares <paramref name="current"/> report with <paramref name="previous"/> (the report
    /// that was "latest" before this refresh), detects health transitions and writes incident
    /// rows to Table Storage. Falls back to loading history when no previous report is supplied.
    /// </summary>
    public async Task DetectAndRecordAsync(AzureReport current, AzureReport? previous = null, CancellationToken ct = default)
    {
        if (previous is null)
        {
            // Legacy path: the caller saved `current` first, so history[1] is the previous run.
            var historyResult = await _repository.LoadHistoryAsync(2, ct);
            if (!historyResult.IsSuccess || historyResult.Value is null || historyResult.Value.Count < 2)
            {
                _logger.LogDebug("Incident detection skipped — not enough history yet");
                return;
            }
            previous = historyResult.Value[1];
        }

        var prevServices = previous.WebServices?.Services ?? new List<WebService>();
        var currServices = current.WebServices?.Services ?? new List<WebService>();

        var prevMap = prevServices.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var incidents = new List<IncidentEntry>();
        // One timestamp per detection run: several services breaking at once is the normal
        // case, and their rows are disambiguated by the service-name RowKey suffix below.
        var occurredAt = DateTime.UtcNow;

        foreach (var svc in currServices)
        {
            if (!prevMap.TryGetValue(svc.Name, out var prev))
                continue;

            var wasHealthy = ServiceHealth.IsHealthy(prev.HttpStatus);
            var isHealthy = ServiceHealth.IsHealthy(svc.HttpStatus);
            var wasBroken = ServiceHealth.IsBroken(prev.HttpStatus);
            var isBroken = ServiceHealth.IsBroken(svc.HttpStatus);

            string? type = null;
            if (wasHealthy && isBroken)
                type = IncidentTypes.NewIncident;
            if (wasBroken && isHealthy)
                type = IncidentTypes.Recovery;

            if (type is null)
                continue;

            var entry = new IncidentEntry
            {
                ServiceName = svc.Name,
                FriendlyName = svc.FriendlyName ?? svc.Name,
                Type = type,
                OccurredAt = occurredAt,
                PreviousStatus = prev.HttpStatus,
                CurrentStatus = svc.HttpStatus,
            };
            incidents.Add(entry);
        }

        if (incidents.Count == 0)
            return;

        _logger.LogInformation("Detected {Count} incident(s) — persisting to Table Storage", incidents.Count);

        // Resolve TableClient supporting both connection string (local/dev) and
        // Managed Identity endpoint (production — no secret needed at runtime).
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return;
        try
        {
            foreach (var inc in incidents)
            {
                // Inverse-ticks prefix gives newest-first ordering; the service-name suffix
                // keeps same-tick incidents from overwriting each other under Upsert Replace.
                var rowKey = new ReverseChronoRowKey(inc.OccurredAt).WithSuffix(SanitizeKey(inc.ServiceName));
                var entity = new TableEntity(TablePartitions.Incidents, rowKey)
                {
                    ["ServiceName"] = inc.ServiceName,
                    ["FriendlyName"] = inc.FriendlyName,
                    ["Type"] = inc.Type,
                    ["OccurredAt"] = inc.OccurredAt,
                    ["PreviousStatus"] = inc.PreviousStatus,
                    ["CurrentStatus"] = inc.CurrentStatus,
                };
                await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
            }

            // Optional: fire webhook
            var webhookUrl = _config["Incidents:WebhookUrl"];
            if (!string.IsNullOrWhiteSpace(webhookUrl))
                await PostWebhookAsync(webhookUrl, incidents, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist incidents to Table Storage");
        }
    }

    /// <summary>Loads the most recent incidents from Table Storage.</summary>
    public async Task<List<IncidentEntry>> LoadRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return new List<IncidentEntry>();

        try
        {
            var entries = new List<IncidentEntry>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{TablePartitions.Incidents}'",
                maxPerPage: limit,
                cancellationToken: ct))
            {
                entries.Add(new IncidentEntry
                {
                    ServiceName = entity.GetString("ServiceName") ?? "",
                    FriendlyName = entity.GetString("FriendlyName") ?? "",
                    Type = entity.GetString("Type") ?? "",
                    OccurredAt = entity.GetDateTimeOffset("OccurredAt")?.UtcDateTime ?? DateTime.MinValue,
                    PreviousStatus = entity.GetString("PreviousStatus"),
                    CurrentStatus = entity.GetString("CurrentStatus"),
                });
                if (entries.Count >= limit)
                    break;
            }

            return entries;
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await tableClient.CreateIfNotExistsAsync(ct);
            return new List<IncidentEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load incidents from Table Storage");
            return new List<IncidentEntry>();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="TableClient"/> for the <c>incidents</c> table using whichever
    /// authentication path is configured:
    /// <list type="bullet">
    ///   <item><c>AzureTableStorage:ConnectionString</c> — used when present (local / Azurite)</item>
    ///   <item><c>AzureTableStorage:Endpoint</c> — used with <see cref="DefaultAzureCredential"/> (production MI)</item>
    /// </list>
    /// Returns <see langword="null"/> and logs a warning when neither is set.
    /// </summary>
    private async Task<TableClient?> GetTableClientAsync(CancellationToken ct)
    {
        if (_cachedClient is not null)
            return _cachedClient;

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_cachedClient is not null)
                return _cachedClient;

            var client = ResolveTableClient();
            if (client is null)
                return null;

            await client.CreateIfNotExistsAsync(ct);
            _cachedClient = client;
            return _cachedClient;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incident table unavailable — incidents will not be persisted this run");
            return null;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private TableClient? ResolveTableClient()
    {
        var connStr = _config["AzureTableStorage:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connStr))
            return new TableClient(connStr, "incidents");

        var endpoint = _config["AzureTableStorage:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
            return new TableClient(new Uri(endpoint), "incidents", new DefaultAzureCredential());

        _logger.LogWarning(
            "AzureTableStorage is not configured (neither ConnectionString nor Endpoint) — incidents will not be persisted");
        return null;
    }

    /// <summary>Strips characters Table Storage forbids in keys ('/', '\', '#', '?', control chars).</summary>
    private static string SanitizeKey(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (c is '/' or '\\' or '#' or '?' || char.IsControl(c))
                continue;
            buffer[length++] = c;
        }
        return length == value.Length ? value : new string(buffer[..length]);
    }

    private async Task PostWebhookAsync(string webhookUrl, List<IncidentEntry> incidents, CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var payload = System.Text.Json.JsonSerializer.Serialize(new { incidents });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(webhookUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Incident webhook returned {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incident webhook POST failed");
        }
    }

    private static bool IsTableMissing(RequestFailedException ex) =>
        ex.Status == 404 && string.Equals(ex.ErrorCode, "TableNotFound", StringComparison.OrdinalIgnoreCase);
}
