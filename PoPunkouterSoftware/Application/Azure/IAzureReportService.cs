using PoShared.Azure;

namespace PoPunkouterSoftware.Application.Azure;

/// <summary>
/// Application-level contract for running a live Azure subscription analysis.
/// SOLID: Single Responsibility — declares only the analysis entry point.
/// SOLID: Dependency Inversion — Features/Infrastructure layer implements; callers consume.
/// GoF:   Facade — hides multi-step Azure SDK orchestration behind one async method.
/// </summary>
public interface IAzureReportService
{
    Task<AzureReport> RunAsync(IProgress<(string Step, int Percent)>? progress = null, CancellationToken ct = default);
}
