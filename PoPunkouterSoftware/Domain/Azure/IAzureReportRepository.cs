using PoShared.Azure;

namespace PoPunkouterSoftware.Domain.Azure;

/// <summary>
/// Domain-level contract for persisting and retrieving the Azure infrastructure report.
/// SOLID: Interface Segregation — only the operations the domain needs are declared here.
/// SOLID: Dependency Inversion — outer layers (Infrastructure) implement; inner layers consume.
/// GoF:   Repository — abstracts data access behind a domain-oriented interface.
/// </summary>
public interface IAzureReportRepository
{
    Task<AzureReport?> LoadAsync(CancellationToken ct = default);
    Task<AzureReport?> LoadPreviousAsync(CancellationToken ct = default);
    Task SaveAsync(AzureReport report, CancellationToken ct = default);
}
