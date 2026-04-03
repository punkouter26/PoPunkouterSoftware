using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using System.Text;
using System.Text.Json;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Persists the Azure report JSON in Table Storage using chunked entities.
/// Each chunk is ≤60 KB so we stay comfortably under the 64 KB property limit.
/// Schema: Table="AzureReport", PartitionKey="latest", RowKey="chunk-000" … "chunk-NNN"
/// </summary>
public class AzureReportStore
{
    private const string TableName        = "AzureReport";
    private const string PartitionKey     = "latest";
    private const int    ChunkMaxBytes    = 60_000; // 60 KB per chunk, well under 64 KB limit

    private readonly ILogger<AzureReportStore> _logger;
    private readonly IConfiguration _config;

    public AzureReportStore(ILogger<AzureReportStore> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Loads and deserialises the latest report from Table Storage. Returns null if not found or Table Storage is not configured.</summary>
    public async Task<AzureReport?> LoadAsync(CancellationToken ct = default)
    {
        var client = GetTableClient();
        if (client is null) return null;

        try
        {
            await client.CreateIfNotExistsAsync(ct);

            var chunks = new List<(string RowKey, string Data)>();
            await foreach (var entity in client.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{PartitionKey}'",
                cancellationToken: ct))
            {
                var data = entity.GetString("Data");
                if (!string.IsNullOrEmpty(data))
                    chunks.Add((entity.RowKey, data));
            }

            if (chunks.Count == 0) return null;

            chunks.Sort((a, b) => string.Compare(a.RowKey, b.RowKey, StringComparison.Ordinal));
            var json = string.Concat(chunks.Select(c => c.Data));

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<AzureReport>(json, opts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AzureReport from Table Storage");
            return null;
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Serialises and saves the report to Table Storage, replacing any previous chunks.</summary>
    public async Task SaveAsync(AzureReport report, CancellationToken ct = default)
    {
        var client = GetTableClient();
        if (client is null)
        {
            _logger.LogWarning("Table Storage not configured — report will not be persisted to Table Storage");
            return;
        }

        try
        {
            await client.CreateIfNotExistsAsync(ct);

            // Delete old chunks first
            await foreach (var old in client.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{PartitionKey}'",
                cancellationToken: ct))
            {
                await client.DeleteEntityAsync(old.PartitionKey, old.RowKey, cancellationToken: ct);
            }

            var json   = JsonSerializer.Serialize(report);
            var bytes  = Encoding.UTF8.GetBytes(json);
            var chunks = SplitIntoChunks(bytes, ChunkMaxBytes);

            var batch = new List<TableTransactionAction>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var entity = new TableEntity(PartitionKey, $"chunk-{i:D3}")
                {
                    ["Data"]        = Encoding.UTF8.GetString(chunks[i]),
                    ["TotalChunks"] = chunks.Count,
                };
                if (i == 0)
                    entity["GeneratedAt"] = report.GeneratedAt ?? DateTime.UtcNow;

                batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, entity));

                // Table Storage batch limit is 100 entities with same PartitionKey
                if (batch.Count == 100)
                {
                    await client.SubmitTransactionAsync(batch, ct);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await client.SubmitTransactionAsync(batch, ct);

            _logger.LogInformation("AzureReport saved to Table Storage in {Count} chunk(s)", chunks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save AzureReport to Table Storage");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private TableClient? GetTableClient()
    {
        var connStr = _config["AzureTableStorage:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(connStr))
            return new TableClient(connStr, TableName);

        var endpoint = _config["AzureTableStorage:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
            return new TableClient(new Uri(endpoint), TableName, new DefaultAzureCredential());

        return null; // Table Storage not configured
    }

    private static List<byte[]> SplitIntoChunks(byte[] data, int chunkSize)
    {
        var chunks = new List<byte[]>();
        for (int offset = 0; offset < data.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - offset);
            var chunk  = new byte[length];
            Buffer.BlockCopy(data, offset, chunk, 0, length);
            chunks.Add(chunk);
        }
        return chunks.Count > 0 ? chunks : new List<byte[]> { Array.Empty<byte>() };
    }
}
