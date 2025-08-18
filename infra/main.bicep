targetScope = 'resourceGroup'

@minLength(1)
@maxLength(64)
@description('Name of the environment that can be used as part of naming resource convention')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
param location string

// Tags that should be applied to all resources.
// 
// Note that 'azd-service-name' tags should be applied separately to service host resources.
// Example usage:
//   tags: union(tags, { 'azd-service-name': <service name in azure.yaml> })
var tags = {
  'azd-env-name': environmentName
}

module resources 'resources.bicep' = {
  name: 'resources'
  params: {
    location: location
    tags: tags
    environmentName: environmentName
  }
}

output AZURE_RESOURCE_WWWROOT_ID string = resources.outputs.AZURE_RESOURCE_WWWROOT_ID
output WWWROOT_URI string = resources.outputs.WWWROOT_URI
output APP_SERVICE_NAME string = resources.outputs.APP_SERVICE_NAME
output RESOURCE_GROUP_ID string = resourceGroup().id
