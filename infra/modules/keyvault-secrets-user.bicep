// Grants a principal the least-privilege "Key Vault Secrets User" role on an
// existing Key Vault. Deployed at the scope of the vault's resource group
// (kv-poshared lives in the shared 'PoShared' RG, not the app's RG), so this is
// invoked as a module with `scope: resourceGroup('PoShared')`.
targetScope = 'resourceGroup'

@description('Name of the existing shared Key Vault, e.g. kv-poshared.')
param keyVaultName string

@description('Object (principal) ID of the identity to grant access to.')
param principalId string

// Built-in role: Key Vault Secrets User (read secret contents only — no write,
// no key/cert management, no management-plane access).
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' existing = {
  name: keyVaultName
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, principalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalType: 'ServicePrincipal'
  }
}
