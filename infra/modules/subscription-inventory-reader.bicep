// Grants a principal the read-only roles the Azure inventory scan needs across the
// whole subscription. Without these the app deploys, boots and reports itself healthy
// while every scan returns an EMPTY report: ARM discovery finds no resources, the cost
// query fails, and /azure renders zeros for services, spend, uptime and history.
//
// That is exactly what production looked like before 2026-09-05: the site's managed
// identity held Key Vault (access policy) and Cognitive Services (RBAC) grants and
// nothing else, so the only two data sources the dashboard exists to read were the two
// it could not touch. The failure is silent by construction — ARM answers an
// unauthorized list with an empty page, not a 403 — which is why it survived so long.
//
// Deployed at SUBSCRIPTION scope: the scan enumerates every resource group, so a
// per-RG grant would only ever show the app its own resource group.
targetScope = 'subscription'

@description('Object (principal) ID of the identity to grant read access to.')
param principalId string

// 'Reader' — list/read every resource in the subscription and nothing else. This is
// what AzureReportService.Discovery/.Inventory/.Metrics/.Security read: sites, plans,
// storage accounts, AI accounts, Log Analytics workspaces, SSL bindings, ARM metrics.
var readerRoleId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'

// 'Cost Management Reader' — the cost query is a separate data plane. Reader does NOT
// cover Microsoft.CostManagement/query/action, which is why the production report
// carried "Cost data unavailable (rate-limited or request failed)" rather than a
// permission error: the call was rejected and the code degrades instead of throwing.
var costManagementReaderRoleId = '72fafb9e-0641-4937-9268-a91bfd8191a3'

// 'Monitoring Reader' — Application Insights / Log Analytics query results are a data
// plane too. Reader grants the ARM control plane over those resources; reading the
// telemetry inside them needs this.
var monitoringReaderRoleId = '43d0d8ad-25c7-4714-9337-8ba259a9fe05'

resource reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // Deterministic name so a re-deploy is a no-op, not a duplicate-assignment error —
  // deploy.yml runs Incremental.
  name: guid(subscription().id, principalId, readerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleId)
    principalId: principalId
    // Stated explicitly so the assignment does not fail while a freshly created managed
    // identity is still replicating through Entra.
    principalType: 'ServicePrincipal'
  }
}

resource costReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, principalId, costManagementReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', costManagementReaderRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource monitoringReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, principalId, monitoringReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringReaderRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
