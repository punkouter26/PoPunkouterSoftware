# PoPunkouterSoftware - Storage Account Security Remediation
# Fixes public blob access on pofacedevsa storage account
# Run: pwsh ./fix-storage-security.ps1

$ErrorActionPreference = 'Stop'

Write-Host "=== Storage Account Security Remediation ===" -ForegroundColor Cyan
Write-Host ""

# Check if logged in to Azure
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) {
    Write-Host "Please run 'az login' first." -ForegroundColor Red
    exit 1
}

Write-Host "Logged in as: $($account.user.name)"
Write-Host "Subscription: $($account.name)"
Write-Host ""

# Fix 1: Disable public blob access on pofacedevsa
Write-Host "[1/1] Fixing pofacedevsa public blob access..." -ForegroundColor Yellow
$storage = az storage account show --name pofacedevsa --resource-group PoShared 2>$null | ConvertFrom-Json
if ($storage) {
    $currentSetting = $storage.allowBlobPublicAccess
    Write-Host "  Current allowBlobPublicAccess: $currentSetting"
    
    if ($currentSetting -eq $true) {
        Write-Host "  Disabling public blob access..."
        az storage account update --name pofacedevsa --resource-group PoShared --allow-blob-public-access false
        Write-Host "  [OK] Public blob access disabled on pofacedevsa" -ForegroundColor Green
    } else {
        Write-Host "  [SKIP] Already disabled" -ForegroundColor Gray
    }
} else {
    Write-Host "  [ERROR] Storage account pofacedevsa not found in PoShared resource group" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Remediation Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Refresh the Azure Dashboard to verify the fix (click 'Refresh from Azure')"
Write-Host "  2. Check Application Insights for any 5xx errors from the affected services"
Write-Host "  3. Investigate broken services (PoHappyTrump, PoPunkouterSoftware, PoRedoImage, PoRepoLineTracker, PoSeeReview)"
