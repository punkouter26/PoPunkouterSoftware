namespace PoPunkouterSoftware.Client;

/// <summary>
/// One row of the Azure dashboard's resource explorer. Built by
/// AzureDashboard.DerivedViews from the raw report and rendered by the
/// AzureResourceExplorer section component; never serialized over the wire.
/// </summary>
public sealed record ResourceExplorerItem(
    string Name,
    string Type,
    string ResourceGroup,
    string Location,
    string Sku,
    string Status,
    string Risk,
    int Requests7d,
    int Errors7d,
    int? ResponseTimeMs
);
