// Azure DNS Zone setup for popunkoutersoftware.com
// This template creates an Azure DNS zone and configures DNS records for the Static Web App

@description('The domain name to configure')
param domainName string = 'popunkoutersoftware.com'

@description('The name of the existing Static Web App')
param staticWebAppName string = 'PoPunkouterSoftware'

@description('The resource group containing the Static Web App')
param staticWebAppResourceGroup string = 'PoShared'

@description('Location for the DNS zone (Global)')
param location string = 'global'

// Reference to existing Static Web App
resource staticWebApp 'Microsoft.Web/staticSites@2022-09-01' existing = {
  name: staticWebAppName
  scope: resourceGroup(staticWebAppResourceGroup)
}

// Create DNS Zone
resource dnsZone 'Microsoft.Network/dnsZones@2018-05-01' = {
  name: domainName
  location: location
  properties: {
    zoneType: 'Public'
  }
  tags: {
    'azd-env-name': 'dns-${domainName}'
    purpose: 'Static Web App Custom Domain'
  }
}

// Create CNAME record for www subdomain pointing to Static Web App
resource wwwCnameRecord 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: dnsZone
  name: 'www'
  properties: {
    TTL: 3600
    CNAMERecord: {
      cname: staticWebApp.properties.defaultHostname
    }
  }
}

// Create A record for apex domain (root domain)
// Note: This will need to be configured after getting the IP from Azure Static Web Apps
resource apexARecord 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: dnsZone
  name: '@'
  properties: {
    TTL: 3600
    ARecords: [
      {
        // This IP will be provided by Azure Static Web Apps when you add the custom domain
        // You'll need to update this after adding the domain in the portal
        ipv4Address: '20.36.45.222' // Default Azure Static Web Apps IP - will be updated
      }
    ]
  }
}

// Outputs
output dnsZoneNameServers array = dnsZone.properties.nameServers
output dnsZoneName string = dnsZone.name
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output resourceGroupName string = resourceGroup().name

// Instructions for next steps
output nextSteps object = {
  step1: 'Update your domain registrar nameservers with the values from dnsZoneNameServers output'
  step2: 'Add custom domain in Azure Portal: Static Web App > Custom domains > Add > Custom domain on Azure DNS'
  step3: 'Select your DNS zone and add both apex and www domains'
  step4: 'Update the A record IP address if different from the default'
}
