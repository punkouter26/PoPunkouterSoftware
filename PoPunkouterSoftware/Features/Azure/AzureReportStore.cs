using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using PoShared.Azure;
using System.Text.Json;

namespace PoPunkouterSoftware.Features.Azure;

/// <summary>
/// Persists the latest Azure report as a single JSON blob.
/// Blob storage keeps the code lean and avoids the chunking logic previously needed for table entities.
/// </summary>
public class AzureReportStore
{
    private const string DefaultContainerName = "azure-report";
    private const string BlobName = "latest-report.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly ILogger<AzureReportStore> _logger;
    private readonly IConfiguration _config;

    public AzureReportStore(ILogger<AzureReportStore> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task<AzureReport?> LoadAsync(CancellationToken ct = default)
    {
        var client = await GetBlobClientAsync(ct);
        if (client is null)
            return null;

        try
        {
            if (!await client.ExistsAsync(ct))
                return null;

            var download = await client.DownloadContentAsync(ct);
            return JsonSerializer.Deserialize<AzureReport>(download.Value.Content.ToString(), _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load AzureReport from Blob Storage");
            return null;
        }
    }

    public async Task SaveAsync(AzureReport report, CancellationToken ct = default)
    {
        var client = await GetBlobClientAsync(ct);
        if (client is null)
        {
            _logger.LogWarning("Blob Storage not configured — report will not be persisted to Blob Storage");
            return;
        }

        try
        {
            using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(report, _jsonOptions));
            await client.UploadAsync(stream, overwrite: true, cancellationToken: ct);
            _logger.LogInformation("AzureReport saved to Blob Storage");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save AzureReport to Blob Storage");
        }
    }

    private async Task<BlobClient?> GetBlobClientAsync(CancellationToken ct)
    {
        var containerName = _config["AzureBlobStorage:ContainerName"] ?? DefaultContainerName;
        var connectionString = _config["AzureBlobStorage:ConnectionString"] ?? _config["AzureTableStorage:ConnectionString"];

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var container = new BlobContainerClient(connectionString, containerName);
            await container.CreateIfNotExistsAsync(publicAccessType: PublicAccessType.None, cancellationToken: ct);
            return container.GetBlobClient(BlobName);
        }

        var endpoint = _config["AzureBlobStorage:Endpoint"] ?? _config["AzureTableStorage:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var service = new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential());
            var container = service.GetBlobContainerClient(containerName);
            await container.CreateIfNotExistsAsync(publicAccessType: PublicAccessType.None, cancellationToken: ct);
            return container.GetBlobClient(BlobName);
        }

        return null;
    }
}
