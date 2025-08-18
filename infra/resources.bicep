@description('The location used for all deployed resources')
param location string = resourceGroup().location

@description('Tags that will be applied to all resources')
param tags object = {}

@description('Environment name to include in resource token')
param environmentName string

var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = uniqueString(subscription().id, resourceGroup().id, location, environmentName)

// Monitor application with Azure Monitor
module monitoring 'br/public:avm/ptn/azd/monitoring:0.1.0' = {
  name: 'monitoring'
  params: {
    logAnalyticsName: '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    applicationInsightsName: '${abbrs.insightsComponents}${resourceToken}'
    applicationInsightsDashboardName: '${abbrs.portalDashboards}${resourceToken}'
    location: location
    tags: tags
  }
}

// Create managed identity for App Service first  
resource appServiceIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${abbrs.managedIdentityUserAssignedIdentities}appservice-${resourceToken}'
  location: location
  tags: tags
}

// Reference to existing App Service Plan in PoShared resource group
resource existingAppServicePlan 'Microsoft.Web/serverfarms@2023-01-01' existing = {
  name: 'PoSharedAppServicePlan'
  scope: resourceGroup('PoShared')
}

// App Service for wwwroot - naming it "PoPunkouterSoftware" as specified
resource wwwrootAppService 'Microsoft.Web/sites@2023-01-01' = {
  name: 'PoPunkouterSoftware'
  location: location
  tags: union(tags, { 'azd-service-name': 'wwwroot' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${appServiceIdentity.id}': {}
    }
  }
  properties: {
    serverFarmId: existingAppServicePlan.id
    httpsOnly: true
    siteConfig: {
      cors: {
        allowedOrigins: ['*']
        supportCredentials: false
      }
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: monitoring.outputs.applicationInsightsConnectionString
        }
        {
          name: 'WEBSITE_NODE_DEFAULT_VERSION'
          value: '~18'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: appServiceIdentity.properties.clientId
        }
      ]
      defaultDocuments: [
        'index.html'
      ]
    }
  }
}

output AZURE_RESOURCE_WWWROOT_ID string = wwwrootAppService.id
output WWWROOT_URI string = 'https://${wwwrootAppService.properties.defaultHostName}'
output APP_SERVICE_NAME string = wwwrootAppService.name
