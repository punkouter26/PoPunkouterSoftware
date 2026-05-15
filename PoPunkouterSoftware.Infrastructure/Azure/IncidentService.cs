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
public sealed class IncidentService
{
    private readonly AzureReportStore _repository;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IncidentService> _logger;

    // Table Storage constants
    private const string PartitionKey = "incidents";

    public IncidentService(
        AzureReportStore repository,
        IConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<IncidentService> logger)
    {
        _repository = repository;
        _config = config;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Compares <paramref name="current"/> report with the previous persisted report,
    /// detects health transitions and writes incident rows to Table Storage.
    /// </summary>
    public async Task DetectAndRecordAsync(AzureReport current, CancellationToken ct = default)
    {
        // Load the previous report from history (second entry — index 1 after saving current)
        var historyResult = await _repository.LoadHistoryAsync(2, ct);
        if (!historyResult.IsSuccess || historyResult.Value is null || historyResult.Value.Count < 2)
        {
            _logger.LogDebug("Incident detection skipped — not enough history yet");
            return;
        }

        var previous = historyResult.Value[1]; // [0] is the newly saved current report

        var prevServices = previous.WebServices?.Services ?? new List<WebService>();
        var currServices = current.WebServices?.Services ?? new List<WebService>();

        var prevMap = prevServices.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var incidents = new List<IncidentEntry>();

        foreach (var svc in currServices)
        {
            if (!prevMap.TryGetValue(svc.Name, out var prev))
                continue;

            var wasHealthy = prev.HttpStatus == "active";
            var isHealthy = svc.HttpStatus == "active";
            var wasBroken = prev.HttpStatus is "broken" or "unreachable";
            var isBroken = svc.HttpStatus is "broken" or "unreachable";

            string? type = null;
            if (wasHealthy && isBroken)
                type = "new-incident";
            if (wasBroken && isHealthy)
                type = "recovery";

            if (type is null)
                continue;

            var entry = new IncidentEntry
            {
                ServiceName = svc.Name,
                FriendlyName = svc.FriendlyName ?? svc.Name,
                Type = type,
                OccurredAt = DateTime.UtcNow,
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
        var tableClient = ResolveTableClient();
        if (tableClient is null)
            return;
        try
        {
            await tableClient.CreateIfNotExistsAsync(ct);

            foreach (var inc in incidents)
            {
                // RowKey uses inverse ticks for newest-first ordering
                var rowKey = (DateTime.MaxValue.Ticks - inc.OccurredAt.Ticks).ToString("D19");
                var entity = new TableEntity(PartitionKey, rowKey)
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
        var tableClient = ResolveTableClient();
        if (tableClient is null)
            return new List<IncidentEntry>();

        try
        {
            var entries = new List<IncidentEntry>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{PartitionKey}'",
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
}
