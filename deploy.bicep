@description('The name of the web app that you wish to update.')
param webAppName string = 'PoPunkouterSoftware'

@description('Location for all resources.')
param location string = 'canadacentral'

@description('The name of Application Insights')
param appInsightsName string = 'PoPunkouterSoftware-appinsights'

@description('The application ID for Application Insights')
param appInsightsAppId string = '17e724a6-a01b-4557-85ed-9037fc5bb43f'

@description('The instrumentation key for Application Insights')
param appInsightsInstrumentationKey string = 'b122ae97-576b-4faa-b23d-35983788ada0'

@description('The name of the App Service Plan to use')
param appServicePlanName string = 'PoSharedFree'

@description('The resource group containing the App Service Plan')
param appServicePlanResourceGroup string = 'PoShared'

@description('The subscription ID containing the App Service Plan')
param subscriptionId string = 'f0504e26-451a-4249-8fb3-46270defdd5b'

// Reference the existing App Service Plan in a different resource group
resource existingAppServicePlan 'Microsoft.Web/serverfarms@2022-03-01' existing = {
  name: appServicePlanName
  scope: resourceGroup(subscriptionId, appServicePlanResourceGroup)
}

// Reference the existing Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

// Reference the existing web app
resource existingWebApp 'Microsoft.Web/sites@2022-03-01' existing = {
  name: webAppName
}

// Update the existing web app to use the referenced App Service Plan
resource webAppUpdate 'Microsoft.Web/sites@2022-03-01' = {
  name: existingWebApp.name
  location: location
  properties: {
    serverFarmId: existingAppServicePlan.id
  }
}

// Update the existing web app's settings with Application Insights connection
resource webAppSettings 'Microsoft.Web/sites/config@2022-03-01' = {
  name: '${existingWebApp.name}/appsettings'
  properties: {
    APPLICATIONINSIGHTS_CONNECTION_STRING: 'InstrumentationKey=${appInsightsInstrumentationKey};IngestionEndpoint=https://canadacentral-0.in.applicationinsights.azure.com/;LiveEndpoint=https://canadacentral.livediagnostics.monitor.azure.com/;ApplicationId=${appInsightsAppId}'
    ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
    XDT_MicrosoftApplicationInsights_Mode: 'default'
  }
  dependsOn: [
    webAppUpdate
  ]
}

output webAppName string = existingWebApp.name
output appInsightsName string = appInsightsName
output appServicePlanName string = existingAppServicePlan.name
output appServicePlanResourceGroup string = appServicePlanResourceGroup
