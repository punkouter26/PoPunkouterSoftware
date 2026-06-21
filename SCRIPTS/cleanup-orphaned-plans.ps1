# PoPunkouterSoftware - Orphaned Resources Cleanup
# Cleans up empty App Service Plans identified in the Azure Dashboard
# Run: pwsh ./cleanup-orphaned-plans.ps1

$ErrorActionPreference = 'Stop'

Write-Host "=== Orphaned Resources Cleanup ===" -ForegroundColor Cyan
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

# List of orphaned resources to clean up
$orphanedPlans = @(
    @{ Name = "asp-pofunquiz-f3"; ResourceGroup = "PoShared"; Reason = "No apps assigned (SKU: F1)" },
    @{ Name = "asp-poissues-f1-westus3"; ResourceGroup = "PoShared"; Reason = "No apps assigned (SKU: F1)" }
)

Write-Host "Orphaned App Service Plans found:" -ForegroundColor Yellow
foreach ($plan in $orphanedPlans) {
    Write-Host "  - $($plan.Name) ($($plan.ResourceGroup)) - $($plan.Reason)"
}
Write-Host ""

# Confirm before deletion
Write-Host "THIS WILL DELETE THE FOLLOWING RESOURCES:" -ForegroundColor Red
Write-Host "  - asp-pofunquiz-f3 (PoShared)" 
Write-Host "  - asp-poissues-f1-westus3 (PoShared)"
Write-Host ""
Write-Host "Commands will be shown but NOT executed (commented out by default)."
Write-Host ""

# Show what would be deleted (commented out for safety)
Write-Host "# === PROPOSED DELETIONS (commented out for safety) ===" -ForegroundColor Gray
Write-Host ""
Write-Host "# az appservice plan delete --name 'asp-pofunquiz-f3' --resource-group 'PoShared' --yes"
Write-Host "# az appservice plan delete --name 'asp-poissues-f1-westus3' --resource-group 'PoShared' --yes"
Write-Host ""

# Interactive deletion (uncomment to enable)
# foreach ($plan in $orphanedPlans) {
#     Write-Host "Deleting $($plan.Name)..."
#     az appservice plan delete --name $plan.Name --resource-group $plan.ResourceGroup --yes
#     Write-Host "  [OK] Deleted $($plan.Name)" -ForegroundColor Green
# }

Write-Host "=== Review Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "To execute deletions, uncomment the deletion block above and run again."
