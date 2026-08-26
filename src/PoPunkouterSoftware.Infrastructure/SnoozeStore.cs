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
    private readonly ILogger<SnoozeStore> _logger;

    // Client bootstrap (lazy creation, single-flight lock, outage cooldown, table recovery)
    // lives in TableClientProvider — it was duplicated verbatim the moment a second small
    // store needed it.
    private readonly TableClientProvider _tables;

    public SnoozeStore(ILogger<SnoozeStore> logger, IConfiguration config)
    {
        _logger = logger;
        _tables = new TableClientProvider(config, logger, "snoozes");
    }

    public async Task<Result<SnoozeEntry>> UpsertAsync(string key, DateTimeOffset expiresAtUtc, string? reason, CancellationToken ct = default)
    {
        var tableClient = await _tables.GetAsync(ct);
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
        catch (RequestFailedException ex) when (TableClientProvider.IsTableMissing(ex))
        {
            await _tables.RecoverMissingTableAsync(ct);
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
        var tableClient = await _tables.GetAsync(ct);
        if (tableClient is null)
            return Result<bool>.Failure("Table client not available - check Azure Table Storage configuration.");

        try
        {
            await tableClient.DeleteEntityAsync(TablePartitions.Snoozes, HashKey(key), cancellationToken: ct);
            return Result<bool>.Success(true);
        }
        catch (RequestFailedException ex) when (TableClientProvider.IsTableMissing(ex))
        {
            await _tables.RecoverMissingTableAsync(ct);
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
        var tableClient = await _tables.GetAsync(ct);
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
        catch (RequestFailedException ex) when (TableClientProvider.IsTableMissing(ex))
        {
            await _tables.RecoverMissingTableAsync(ct);
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
}
