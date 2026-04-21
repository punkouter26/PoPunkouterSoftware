using Azure.Data.Tables;
using Azure.Identity;
using PoShared.Azure;
using PoPunkouterSoftware.Domain.Azure;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Persists the latest Azure report as a single entity in Azure Table Storage.
/// Uses the app-specific storage account (not the shared PoShared one).
/// SOLID: Single Responsibility — this class has one job: persist/load the Azure report.
/// SOLID: Open/Closed — storage strategy can be extended without modifying callers.
/// GoF:   Repository — implements IAzureReportRepository, hiding Table Storage details from the domain.
/// </summary>
public class AzureReportStore : IAzureReportRepository
{
    private const string DefaultTableName   = "AzureReport";
    private const string PartitionKey       = "report";
    private const string RowKey             = "latest";
    private const string HistoryPartitionKey = "history";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly ILogger<AzureReportStore> _logger;
    private readonly IConfiguration            _config;

    public AzureReportStore(ILogger<AzureReportStore> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<AzureReport?> LoadAsync(CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return null;

        try
        {
            var response = await tableClient.GetEntityIfExistsAsync<TableEntity>(PartitionKey, RowKey, cancellationToken: ct);
            if (!response.HasValue)
                return null;

            var json = DecompressEntity(response.Value!);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<AzureReport>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AzureReport from Table Storage");
            return null;
        }
    }

    public async Task SaveAsync(AzureReport report, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
        {
            _logger.LogWarning("Table Storage not configured — report will not be persisted");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(report, _jsonOptions);
            var compressed = CompressJson(json);

            var entity = new TableEntity(PartitionKey, RowKey)
            {
                ["ReportJsonGz"] = compressed,
                ["SavedAt"]      = DateTimeOffset.UtcNow,
            };

            await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);

            // Also save a timestamped history entry (inverse ticks = newest first in queries)
            var histRowKey    = (DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UtcNow.Ticks).ToString("D20");
            var historyEntity = new TableEntity(HistoryPartitionKey, histRowKey)
            {
                ["ReportJsonGz"] = compressed,
                ["SavedAt"]      = DateTimeOffset.UtcNow,
            };
            await tableClient.UpsertEntityAsync(historyEntity, TableUpdateMode.Replace, ct);

            _logger.LogInformation("AzureReport saved to Table Storage (including history)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save AzureReport to Table Storage");
        }
    }

    public async Task<AzureReport?> LoadPreviousAsync(CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return null;

        try
        {
            // History RowKey uses inverse ticks so the first result is the most recent entry
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{HistoryPartitionKey}'",
                maxPerPage: 1,
                cancellationToken: ct))
            {
                var json = DecompressEntity(entity);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return JsonSerializer.Deserialize<AzureReport>(json, _jsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load previous AzureReport from history");
        }
        return null;
    }

    private static string CompressJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(output.ToArray());
    }

    private static string? DecompressEntity(TableEntity entity)
    {
        // Try compressed format first, fall back to legacy plain JSON
        var compressed = entity.GetString("ReportJsonGz");
        if (!string.IsNullOrWhiteSpace(compressed))
        {
            var bytes = Convert.FromBase64String(compressed);
            using var input  = new MemoryStream(bytes);
            using var gz     = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return entity.GetString("ReportJson");
    }

    private async Task<TableClient?> GetTableClientAsync(CancellationToken ct)
    {
        var tableName        = _config["AzureTableStorage:TableName"] ?? DefaultTableName;
        var connectionString = _config["AzureTableStorage:ConnectionString"];

        TableServiceClient serviceClient;

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            serviceClient = new TableServiceClient(connectionString);
        }
        else
        {
            var endpoint = _config["AzureTableStorage:Endpoint"];
            if (string.IsNullOrWhiteSpace(endpoint))
                return null;

            serviceClient = new TableServiceClient(new Uri(endpoint), new DefaultAzureCredential());
        }

        var tableClient = serviceClient.GetTableClient(tableName);
        await tableClient.CreateIfNotExistsAsync(ct);
        return tableClient;
    }
}
