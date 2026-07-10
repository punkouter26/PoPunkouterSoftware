using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Infrastructure.Azure;

/// <summary>
/// Locates and parses the on-disk azure-full-report.json cache — the degradation path
/// used when Table Storage is unavailable. Owned by Infrastructure so feature slices
/// (Diag, Portfolio) can share it without depending on each other.
/// </summary>
public static class ReportFileCache
{
    public const string ReportFileName = "azure-full-report.json";

    // In the unified Blazor WASM model, wwwroot lives in the Client project.
    // env.WebRootPath is null on the server because the server has no wwwroot of its own.
    // In dev, resolve to the Client project's wwwroot; in production, UseStaticWebAssets()
    // publishes client assets under ContentRootPath/wwwroot, so WebRootPath is non-null.
    public static string GetDataDir(IWebHostEnvironment env) =>
        env.WebRootPath is not null
            ? Path.Combine(env.WebRootPath, "data")
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "PoPunkouterSoftware.Client", "wwwroot", "data"));

    public static string GetReportPath(IWebHostEnvironment env) =>
        Path.Combine(GetDataDir(env), ReportFileName);

    // Hoisted so System.Text.Json's converter metadata cache is reused instead of being
    // rebuilt for every request that hits the file-fallback path.
    private static readonly JsonSerializerOptions FileReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses a cached report file, treating corruption as "no report" instead of letting a
    /// <see cref="JsonException"/> bubble into a 500 — the file cache is the degradation path
    /// used exactly when Table Storage is already down.
    /// </summary>
    public static AzureReport? TryDeserializeReport(string json, ILogger logger)
    {
        try
        {
            return JsonSerializer.Deserialize<AzureReport>(json, FileReadJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Cached azure-full-report.json is corrupt — ignoring the file cache");
            return null;
        }
    }
}
