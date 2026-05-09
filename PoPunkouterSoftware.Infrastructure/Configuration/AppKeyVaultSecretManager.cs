using Azure.Security.KeyVault.Secrets;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Microsoft.Extensions.Configuration;

namespace PoPunkouterSoftware.Infrastructure;

/// <summary>
/// Loads all enabled Key Vault secrets and maps names that use "--"
/// into standard .NET configuration key delimiters.
///
/// Example:
///   KV secret "ApplicationInsights--ConnectionString"
///   → config key "ApplicationInsights:ConnectionString"
///
/// </summary>
public sealed class AppKeyVaultSecretManager : KeyVaultSecretManager
{
    public override bool Load(SecretProperties secret) =>
        secret.Enabled ?? true;

    public override string GetKey(KeyVaultSecret secret) =>
        secret.Name.Replace("--", ConfigurationPath.KeyDelimiter);
}
