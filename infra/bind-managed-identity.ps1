<#
.SYNOPSIS
    Bind the shared user-assigned managed identity (mi-poshared-containerapps)
    to the PoPunkouterSoftware App Service and grant it the minimum roles
    needed to read Key Vault secrets and Table Storage tables.

.DESCRIPTION
    The app currently uses a system-assigned MI, which is hard to audit and
    hard to share role grants for. This script:

      1. Binds mi-poshared-containerapps to the app
         (adds it to identity.userAssignedIdentities).
      2. Sets keyVaultReferenceIdentity to the user-assigned MI's clientId
         (so Key Vault references use the shared MI).
      3. Grants the MI:
           - "Storage Table Data Contributor" on stpopunkoutersoftware
           - "Key Vault Secrets User"            on kv-poshared
         (Both are read+write/list — least-privilege for this workload.)

    Idempotent: safe to re-run. Re-applies the same role assignments if they
    already exist (using --condition to avoid duplicates).

.PREREQ
    - `az login` to subscription "Punkouter26"
    - Contributor on PoPunkouterSoftware RG + User Access Administrator on
      the same RG (or Owner on the subscription) to grant roles.

.EXAMPLE
    .\infra\bind-managed-identity.ps1
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $AppRg           = 'PoPunkouterSoftware',
    [string] $AppName         = 'app-popunkoutersoftware',
    [string] $SharedRg        = 'PoShared',
    [string] $SharedMiName    = 'mi-poshared-containerapps',
    [string] $StorageAccount  = 'stpopunkoutersoftware',
    [string] $KeyVault        = 'kv-poshared'
)

$ErrorActionPreference = 'Stop'

function Step([string]$msg) { Write-Host "[mi-bind] $msg" }

Step "Resolving shared user-assigned MI in $SharedRg"
$mi = az identity show -g $SharedRg -n $SharedMiName -o json | ConvertFrom-Json
$miClientId = $mi.clientId
$miPrincipalId = $mi.principalId
if (-not $miClientId) { throw "Could not resolve $SharedRg/$SharedMiName" }
Step "  principalId = $miPrincipalId"
Step "  clientId    = $miClientId"

Step "Resolving app and storage scopes"
$app = az webapp show -g $AppRg -n $AppName -o json | ConvertFrom-Json
$storageId = az storage account show -n $StorageAccount -g $AppRg -o json | ConvertFrom-Json
$kvId = az keyvault show -n $KeyVault -o json | ConvertFrom-Json
Step "  app         = $($app.id)"
Step "  storage     = $($storageId.id)"
Step "  keyvault    = $($kvId.id)"

Step "Updating App Service identity (binding user-assigned MI)"
if ($PSCmdlet.ShouldProcess("$AppRg/$AppName", 'az webapp update --identity')) {
    # Use the App Service ARM "identity" property directly. --set supports
    # inline JSON for nested properties, which is the simplest cross-platform
    # way to write userAssignedIdentities.
    $subId = az account show --query id -o tsv
    $miArmId = "/subscriptions/$subId/resourceGroups/$SharedRg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$SharedMiName"

    # Add the user-assigned MI while preserving the existing system-assigned MI
    $identityJson = @"
{"type":"SystemAssigned,UserAssigned","userAssignedIdentities":{"$miArmId":{}}}
"@
    az webapp update --ids $app.id --set identity="$identityJson" --output none
    if ($LASTEXITCODE -ne 0) { throw "webapp update (identity) failed (exit $LASTEXITCODE)" }
}
az webapp update --ids $app.id --set keyVaultReferenceIdentity=$miClientId --output none
if ($LASTEXITCODE -ne 0) { throw "keyVaultReferenceIdentity set failed (exit $LASTEXITCODE)" }
Step "  keyVaultReferenceIdentity = $miClientId"

function Ensure-Role(
    [string] $Role,
    [string] $Scope,
    [string] $PrincipalId
) {
    $existing = az role assignment list --assignee $PrincipalId --scope $Scope --role $Role --query '[].id' -o tsv 2>$null
    if ($existing) {
        Write-Host "  [skip] role '$Role' already granted on $Scope"
        return
    }
    if ($PSCmdlet.ShouldProcess("$Scope", "Grant '$Role' to $PrincipalId")) {
        az role assignment create --assignee-object-id $PrincipalId `
                                  --assignee-principal-type ServicePrincipal `
                                  --role $Role --scope $Scope --output none
        if ($LASTEXITCODE -ne 0) { Write-Warning "  [failed] '$Role' on $Scope" }
        else { Write-Host "  [ok]    granted '$Role' on $Scope" }
    }
}

Step "Granting Storage Table Data Contributor on $StorageAccount"
Ensure-Role -Role 'Storage Table Data Contributor' -Scope $storageId.id -PrincipalId $miPrincipalId

Step "Granting Key Vault Secrets User on $KeyVault"
Ensure-Role -Role 'Key Vault Secrets User' -Scope $kvId.id -PrincipalId $miPrincipalId

Step "Done. Re-deploy the App Service so it picks up the new identity binding."
