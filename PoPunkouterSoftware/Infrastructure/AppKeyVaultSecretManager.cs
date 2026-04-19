using Azure.Security.KeyVault.Secrets;
using Azure.Extensions.AspNetCore.Configuration.Secrets;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Filters Key Vault secrets to only those prefixed with "PoPunkouterSoftware--"
/// and strips that prefix so they map to standard .NET configuration keys.
///
/// Example:
///   KV secret "PoPunkouterSoftware--ApplicationInsights--ConnectionString"
///   → config key "ApplicationInsights:ConnectionString"
///
/// SOLID: Single Responsibility — this class has one job: translate KV secret names.
/// SOLID: Open/Closed — extend Key Vault filtering by subclassing, not editing this class.
/// GoF:   Adapter — adapts Azure Key Vault's flat naming convention to .NET's
///        hierarchical configuration key format.
/// </summary>
internal sealed class AppKeyVaultSecretManager : KeyVaultSecretManager
{
    private const string Prefix = "PoPunkouterSoftware--";

    public override bool Load(SecretProperties secret) =>
        secret.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public override string GetKey(KeyVaultSecret secret) =>
        secret.Name[Prefix.Length..].Replace("--", ConfigurationPath.KeyDelimiter);
}
