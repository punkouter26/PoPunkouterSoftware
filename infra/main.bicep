// Infrastructure for PoPunkouterSoftware.
//
// Faithful, idempotent description of the resources that already exist in the
// 'PoPunkouterSoftware' resource group (subscription bbb8dfbe-...-fbf861b51037,
// region West US 2). Deployed in Incremental mode from deploy.yml, so a run
// against the live resource group is a no-op when nothing has drifted.
//
// IMPORTANT: appSettings are intentionally NOT declared on the site resource.
// The running app sources its settings (Key Vault references, the Application
// Insights connection string, etc.) from the portal / runtime. In Incremental
// mode, omitting the appSettings collection leaves those existing settings
// untouched — declaring it here would replace the whole set and wipe them.
targetScope = 'resourceGroup'

@description('App Service name. Must match the live site — defaults to the production app.')
param appName string = 'app-popunkoutersoftware-win'

@description('App Service Plan (Windows) hosting the site.')
param appServicePlanName string = 'asp-PoPunkouterSoftware-f1'

@description('Storage account used by the app (Azure Table Storage).')
param storageAccountName string = 'stpopunkoutersoftware'

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('App Service Plan SKU. F1 = Free tier (no Always-On, cold starts).')
param appServicePlanSku string = 'F1'

// Windows App Service Plan, Free (F1) tier.
resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  kind: 'app'
  sku: {
    name: appServicePlanSku
    tier: appServicePlanSku == 'F1' ? 'Free' : 'Basic'
  }
  properties: {
    reserved: false // false = Windows
  }
}

// The site. Windows, .NET 10, HTTPS-only, TLS 1.2, system-assigned identity
// (used to read Key Vault 'kv-poshared'). Always-On stays off — F1 cannot run it.
resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      alwaysOn: false
      metadata: [
        {
          name: 'CURRENT_STACK'
          value: 'dotnet'
        }
      ]
    }
  }
}

// Azure Table Storage backing store. HTTPS-only, TLS 1.2, no public blob access.
resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

output appName string = site.name
output appServicePlanId string = appServicePlan.id
output sitePrincipalId string = site.identity.principalId
