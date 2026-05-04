using PoPunkouterSoftware.Shared.Azure;

namespace PoPunkouterSoftware.Application.Azure;

/// <summary>
/// Application-level contract for running a live Azure subscription analysis.
/// </summary>
public interface IAzureReportService
{
    Task<AzureReport> RunAsync(IProgress<(string Step, int Percent)>? progress = null, CancellationToken ct = default);
}
