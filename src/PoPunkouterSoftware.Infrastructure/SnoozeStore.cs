using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared;
using System.Security.Cryptography;
using System.Text;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Persists "snoozed" findings — an opaque client-owned key, an expiry, and an optional
/// reason — in the <see cref="TablePartitions.Snoozes"/> partition of the same table
/// <see cref="AzureReportStore"/> uses. Deliberately much simpler than
/// <see cref="AzureReportStore"/>: entities are tiny (no gzip, no history rows) and there
/// is no local-file fallback — that fallback exists to dodge the 64 KiB blob-size limit on
/// the big report, which does not apply here. If Table Storage is unavailable, callers see
/// a failed <see cref="Result{T}"/> like any other transient Table Storage outage.
/// </summary>
public class SnoozeStore
{
    private const string DefaultTableName = "PoPunkouterSoftwareReport";

    private readonly ILogger<SnoozeStore> _logger;
    private readonly IConfiguration _config;

    // Cached after first successful init so CreateIfNotExistsAsync is not called on every request.
    private TableClient? _cachedClient;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // When client creation fails (storage outage), short-circuit for a cooldown window instead
    // of serializing every request behind the semaphore while each pays a full connect timeout.
    private DateTimeOffset _lastClientFailureAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ClientRetryCooldown = TimeSpan.FromSeconds(30);

    public SnoozeStore(ILogger<SnoozeStore> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<Result<SnoozeEntry>> UpsertAsync(string key, DateTimeOffset expiresAtUtc, string? reason, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<SnoozeEntry>.Failure("Table client not available - check Azure Table Storage configuration.");

        try
        {
            var createdAtUtc = DateTimeOffset.UtcNow;
            var entity = new TableEntity(TablePartitions.Snoozes, HashKey(key))
            {
                ["Key"] = key,
                ["ExpiresAtUtc"] = expiresAtUtc,
                ["Reason"] = reason,
                ["CreatedAtUtc"] = createdAtUtc,
            };

            await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
            return Result<SnoozeEntry>.Success(new SnoozeEntry(key, expiresAtUtc, reason, createdAtUtc));
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await RecoverMissingTableAsync(ct);
            return Result<SnoozeEntry>.Failure("Snooze table was missing and has been recreated - please retry.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save snooze entry to Table Storage");
            return Result<SnoozeEntry>.Failure("Failed to save snooze entry to Table Storage", ex);
        }
    }

    /// <summary>Idempotent — a missing row is not an error, so callers can un-snooze freely.</summary>
    public async Task<Result<bool>> RemoveAsync(string key, CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<bool>.Failure("Table client not available - check Azure Table Storage configuration.");

        try
        {
            await tableClient.DeleteEntityAsync(TablePartitions.Snoozes, HashKey(key), cancellationToken: ct);
            return Result<bool>.Success(true);
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await RecoverMissingTableAsync(ct);
            return Result<bool>.Success(true);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone (or never existed) — un-snoozing is idempotent.
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove snooze entry from Table Storage");
            return Result<bool>.Failure("Failed to remove snooze entry from Table Storage", ex);
        }
    }

    /// <summary>
    /// Returns every non-expired snooze. The partition is small (one row per snoozed
    /// finding), so filtering expiry in code rather than in a server-side OData filter
    /// keeps this simple and avoids clock-skew edge cases in the query string.
    /// </summary>
    public async Task<Result<List<SnoozeEntry>>> GetActiveAsync(CancellationToken ct = default)
    {
        var tableClient = await GetTableClientAsync(ct);
        if (tableClient is null)
            return Result<List<SnoozeEntry>>.Failure("Table client not available - check Azure Table Storage configuration.");

        var results = new List<SnoozeEntry>();
        try
        {
            var now = DateTimeOffset.UtcNow;
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{TablePartitions.Snoozes}'",
                cancellationToken: ct))
            {
                var entry = ToSnoozeEntry(entity);
                if (entry is not null && entry.ExpiresAtUtc > now)
                    results.Add(entry);
            }

            return Result<List<SnoozeEntry>>.Success(results);
        }
        catch (RequestFailedException ex) when (IsTableMissing(ex))
        {
            await RecoverMissingTableAsync(ct);
            return Result<List<SnoozeEntry>>.Success(results);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load snooze entries from Table Storage");
            return Result<List<SnoozeEntry>>.Failure("Failed to load snooze entries from Table Storage", ex);
        }
    }

    private SnoozeEntry? ToSnoozeEntry(TableEntity entity)
    {
        var key = entity.GetString("Key");
        if (string.IsNullOrEmpty(key))
        {
            // A corrupt/legacy row must degrade to "skip it", never a 500.
            _logger.LogWarning("Skipping corrupt snooze entity {PartitionKey}/{RowKey}", entity.PartitionKey, entity.RowKey);
            return null;
        }

        var expiresAtUtc = entity.GetDateTimeOffset("ExpiresAtUtc") ?? DateTimeOffset.MinValue;
        var createdAtUtc = entity.GetDateTimeOffset("CreatedAtUtc") ?? DateTimeOffset.MinValue;
        var reason = entity.GetString("Reason");
        return new SnoozeEntry(key, expiresAtUtc, reason, createdAtUtc);
    }

    /// <summary>
    /// Table Storage RowKey forbids '/', '\', '#', '?' and caps length at 1 KiB — the
    /// client's opaque key can contain arbitrary characters (e.g. '|'), so hash it to a
    /// fixed-length, always-legal RowKey. The raw key is stored as a property so it
    /// round-trips in list responses.
    /// </summary>
    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(bytes);
    }

    private async Task<TableClient?> GetTableClientAsync(CancellationToken ct)
    {
        if (_cachedClient is not null)
            return _cachedClient;

        // Storage is down or misconfigured: don't stampede — one caller retries per cooldown.
        if (DateTimeOffset.UtcNow - _lastClientFailureAt < ClientRetryCooldown)
            return null;

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_cachedClient is not null)
                return _cachedClient;
            if (DateTimeOffset.UtcNow - _lastClientFailureAt < ClientRetryCooldown)
                return null;

            _cachedClient = await CreateTableClientAsync(ct);
            if (_cachedClient is null)
                _lastClientFailureAt = DateTimeOffset.UtcNow;
            return _cachedClient;
        }
        catch (Exception ex)
        {
            _lastClientFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning("Table Storage unavailable for snoozes. Reason: {Reason}", ex.Message);
            return null;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<TableClient?> CreateTableClientAsync(CancellationToken ct)
    {
        var tableName = _config["AzureTableStorage:TableName"] ?? DefaultTableName;
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

    private async Task RecoverMissingTableAsync(CancellationToken ct)
    {
        _logger.LogInformation("Azure report table was missing. Recreating it so snooze reads/writes can continue.");

        await _clientLock.WaitAsync(ct);
        try
        {
            _cachedClient = await CreateTableClientAsync(ct);
        }
        catch (Exception ex)
        {
            _cachedClient = null;
            _lastClientFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning(ex, "Could not recreate missing Azure report table");
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private static bool IsTableMissing(RequestFailedException ex) =>
        ex.Status == 404 && string.Equals(ex.ErrorCode, "TableNotFound", StringComparison.OrdinalIgnoreCase);
}
