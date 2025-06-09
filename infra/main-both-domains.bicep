// Azure DNS Zone setup for both popunkoutersoftware.com and punkoutersoftware.com
// This template creates Azure DNS zones and configures DNS records for the Static Web App

@description('The primary domain name to configure')
param primaryDomainName string = 'popunkoutersoftware.com'

@description('The secondary domain name to configure (will redirect to primary)')
param secondaryDomainName string = 'punkoutersoftware.com'

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

// Create DNS Zone for primary domain
resource primaryDnsZone 'Microsoft.Network/dnsZones@2018-05-01' = {
  name: primaryDomainName
  location: location
  properties: {
    zoneType: 'Public'
  }
  tags: {
    'azd-env-name': 'dns-${primaryDomainName}'
    purpose: 'Static Web App Primary Domain'
  }
}

// Create DNS Zone for secondary domain
resource secondaryDnsZone 'Microsoft.Network/dnsZones@2018-05-01' = {
  name: secondaryDomainName
  location: location
  properties: {
    zoneType: 'Public'
  }
  tags: {
    'azd-env-name': 'dns-${secondaryDomainName}'
    purpose: 'Static Web App Secondary Domain (Redirect)'
  }
}

// Primary domain records
// Create CNAME record for www subdomain pointing to Static Web App
resource primaryWwwCnameRecord 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: primaryDnsZone
  name: 'www'
  properties: {
    TTL: 3600
    CNAMERecord: {
      cname: staticWebApp.properties.defaultHostname
    }
  }
}

// Create A record for primary apex domain
resource primaryApexARecord 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: primaryDnsZone
  name: '@'
  properties: {
    TTL: 3600
    ARecords: [
      {
        ipv4Address: '20.36.45.222' // Azure Static Web Apps IP
      }
    ]
  }
}

// Secondary domain records (redirect to primary)
// Create CNAME record for www subdomain redirecting to primary domain
resource secondaryWwwCnameRecord 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = {
  parent: secondaryDnsZone
  name: 'www'
  properties: {
    TTL: 3600
    CNAMERecord: {
      cname: 'www.${primaryDomainName}'
    }
  }
}

// For the secondary apex domain, we'll use the same IP and handle redirects at the app level
resource secondaryApexARecord 'Microsoft.Network/dnsZones/A@2018-05-01' = {
  parent: secondaryDnsZone
  name: '@'
  properties: {
    TTL: 3600
    ARecords: [
      {
        ipv4Address: '20.36.45.222' // Same IP, redirect at app level
      }
    ]
  }
}

// Outputs
output primaryDnsZoneNameServers array = primaryDnsZone.properties.nameServers
output secondaryDnsZoneNameServers array = secondaryDnsZone.properties.nameServers
output primaryDnsZoneName string = primaryDnsZone.name
output secondaryDnsZoneName string = secondaryDnsZone.name
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output resourceGroupName string = resourceGroup().name

// Instructions for next steps
output nextSteps object = {
  step1: 'Purchase both domains from your preferred registrar'
  step2_primary: 'Update ${primaryDomainName} registrar nameservers with: ${primaryDnsZone.properties.nameServers}'
  step3_secondary: 'Update ${secondaryDomainName} registrar nameservers with: ${secondaryDnsZone.properties.nameServers}'
  step4: 'Add custom domains in Azure Portal for the primary domain only (2 domain limit)'
  step5: 'Configure redirects in your app to handle ${secondaryDomainName} -> ${primaryDomainName}'
  note: 'Due to Static Web App limits, only the primary domain can be added directly. Secondary domain will redirect via DNS.'
}
