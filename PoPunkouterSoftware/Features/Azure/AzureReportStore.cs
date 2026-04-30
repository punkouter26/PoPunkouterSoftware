using Azure.Data.Tables;
using Azure.Identity;
using PoPunkouterSoftware.Shared.Azure;
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
    private const string DefaultTableName   = "PoPunkouterSoftwareReport";
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

    // Cached after first successful init so CreateIfNotExistsAsync is not called on every request.
    private TableClient? _cachedClient;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    public AzureReportStore(ILogger<AzureReportStore> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<Result<AzureReport?>> LoadAsync(CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<AzureReport?>.Failure("Table client not available - check Azure Table Storage configuration.");

        try
        {
            var response = await tableClient.GetEntityIfExistsAsync<TableEntity>(PartitionKey, RowKey, cancellationToken: ct);
            if (!response.HasValue)
                return Result<AzureReport?>.Success(null);

            var json = DecompressEntity(response.Value!);
            if (string.IsNullOrWhiteSpace(json))
                return Result<AzureReport?>.Success(null);

            var report = JsonSerializer.Deserialize<AzureReport>(json, _jsonOptions);
            return Result<AzureReport?>.Success(report);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AzureReport from Table Storage");
            return Result<AzureReport?>.Failure("Failed to load AzureReport from Table Storage", ex);
        }
    }

    public async Task<Result<bool>> SaveAsync(AzureReport report, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
        {
            _logger.LogWarning("Table Storage not configured — report will not be persisted");
            return Result<bool>.Failure("Table Storage not configured");
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
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save AzureReport to Table Storage");
            return Result<bool>.Failure("Failed to save AzureReport to Table Storage", ex);
        }
    }

    public async Task<Result<List<AzureReport>>> LoadHistoryAsync(int maxEntries = 90, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<List<AzureReport>>.Failure("Table client not available - check Azure Table Storage configuration.");

        var results = new List<AzureReport>();
        try
        {
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{HistoryPartitionKey}'",
                maxPerPage: maxEntries,
                cancellationToken: ct))
            {
                var json = DecompressEntity(entity);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var report = JsonSerializer.Deserialize<AzureReport>(json, _jsonOptions);
                if (report is not null)
                    results.Add(report);
                if (results.Count >= maxEntries) break;
            }
            return Result<List<AzureReport>>.Success(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AzureReport history from Table Storage");
            return Result<List<AzureReport>>.Failure("Failed to load AzureReport history from Table Storage", ex);
        }
    }

    public async Task<Result<AzureReport?>> LoadPreviousAsync(CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<AzureReport?>.Failure("Table client not available - check Azure Table Storage configuration.");

        try
        {
            // History RowKey uses inverse ticks so the first result is the most recent entry
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{HistoryPartitionKey}'",
                maxPerPage: 1,
                cancellationToken: ct))
            {
                var json = DecompressEntity(entity);
                if (string.IsNullOrWhiteSpace(json)) return Result<AzureReport?>.Success(null);
                var report = JsonSerializer.Deserialize<AzureReport>(json, _jsonOptions);
                return Result<AzureReport?>.Success(report);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load previous AzureReport from history");
            return Result<AzureReport?>.Failure("Failed to load previous AzureReport from history", ex);
        }
        return Result<AzureReport?>.Success(null);
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
        if (_cachedClient is not null)
            return _cachedClient;

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_cachedClient is not null)
                return _cachedClient;

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
            _cachedClient = tableClient;
            return _cachedClient;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Table Storage unavailable — report will fall back to file");
            return null;
        }
        finally
        {
            _clientLock.Release();
        }
    }
}
