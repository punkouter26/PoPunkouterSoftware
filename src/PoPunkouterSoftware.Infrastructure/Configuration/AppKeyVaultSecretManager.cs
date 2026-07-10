using Azure.Security.KeyVault.Secrets;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Microsoft.Extensions.Configuration;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Loads all enabled Key Vault secrets and maps names that use "--"
/// into standard .NET configuration key delimiters.
///
/// Secrets prefixed with "PoPunkouterSoftware--" have the prefix stripped so
/// they resolve to the expected configuration section paths.
///
/// Examples:
///   KV secret "PoPunkouterSoftware--AzureTableStorage--Endpoint"
///   → config key "AzureTableStorage:Endpoint"
///
///   KV secret "AzureAd--ClientSecret"
///   → config key "AzureAd:ClientSecret"
/// </summary>
public sealed class AppKeyVaultSecretManager : KeyVaultSecretManager
{
    private const string AppPrefix = "PoPunkouterSoftware--";

    public override bool Load(SecretProperties secret) =>
        secret.Enabled ?? true;

    public override string GetKey(KeyVaultSecret secret)
    {
        var name = secret.Name.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase)
            ? secret.Name[AppPrefix.Length..]
            : secret.Name;
        return name.Replace("--", ConfigurationPath.KeyDelimiter);
    }
}
