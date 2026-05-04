using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Domain.Azure;

/// <summary>
/// Domain-level contract for persisting and retrieving the Azure infrastructure report.
/// </summary>
public interface IAzureReportRepository
{
    Task<Result<AzureReport?>> LoadAsync(CancellationToken ct = default);
    Task<Result<AzureReport?>> LoadPreviousAsync(CancellationToken ct = default);
    Task<Result<bool>> SaveAsync(AzureReport report, CancellationToken ct = default);
    /// <summary>Returns up to <paramref name="maxEntries"/> historical reports, newest first.</summary>
    Task<Result<List<AzureReport>>> LoadHistoryAsync(int maxEntries = 90, CancellationToken ct = default);
}
