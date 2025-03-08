param(
    [string]$subscriptionId = "f0504e26-451a-4249-8fb3-46270defdd5b",
    [string]$resourceGroupName = "PoPunkouterSoftware",
    [string]$location = "canadacentral",
    [string]$webAppName = "PoPunkouterSoftware",
    [string]$appInsightsName = "PoPunkouterSoftware-appinsights",
    [string]$appInsightsInstrumentationKey = "b122ae97-576b-4faa-b23d-35983788ada0",
    [string]$appInsightsAppId = "17e724a6-a01b-4557-85ed-9037fc5bb43f",
    [string]$appServicePlanName = "PoSharedFree",
    [string]$appServicePlanResourceGroup = "PoShared"
)

# Set error action preference
$ErrorActionPreference = "Stop"

# Display script banner
Write-Host "====================================================="
Write-Host "Punkouter Software - Azure Deployment Script"
Write-Host "====================================================="
Write-Host ""

# Step 1: Verify Azure CLI is installed
try {
    $azVersion = az --version
    Write-Host "✅ Azure CLI is installed" -ForegroundColor Green
} 
catch {
    Write-Host "❌ Azure CLI is not installed. Please install Azure CLI before running this script." -ForegroundColor Red
    Write-Host "   Visit: https://docs.microsoft.com/en-us/cli/azure/install-azure-cli" -ForegroundColor Red
    exit
}

# Step 2: Login to Azure (will open browser if not already logged in)
Write-Host "🔐 Logging in to Azure..." -ForegroundColor Yellow
az login

# Step 3: Set the subscription context
Write-Host "🔄 Setting subscription context..." -ForegroundColor Yellow
az account set --subscription "$subscriptionId"

# Step 4: Verify Resource Groups exist
Write-Host "🔍 Verifying resource groups..." -ForegroundColor Yellow
$appRgExists = (az group exists --name "$resourceGroupName") -eq "true"
$planRgExists = (az group exists --name "$appServicePlanResourceGroup") -eq "true"

if (-not $appRgExists) {
    Write-Host "❌ Resource Group '$resourceGroupName' doesn't exist. Please check your Azure portal." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "✅ Web app resource group '$resourceGroupName' exists" -ForegroundColor Green
}

if (-not $planRgExists) {
    Write-Host "❌ Resource Group '$appServicePlanResourceGroup' containing the app service plan doesn't exist." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "✅ App service plan resource group '$appServicePlanResourceGroup' exists" -ForegroundColor Green
}

# Step 5: Verify the App Service Plan exists
Write-Host "🔍 Verifying App Service Plan..." -ForegroundColor Yellow
$appServicePlan = az appservice plan show --name "$appServicePlanName" --resource-group "$appServicePlanResourceGroup" 2>$null
if (-not $appServicePlan) {
    Write-Host "❌ App Service Plan '$appServicePlanName' not found in resource group '$appServicePlanResourceGroup'." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "✅ Found existing App Service Plan: $appServicePlanName in resource group $appServicePlanResourceGroup" -ForegroundColor Green
}

# Step 6: Verify Application Insights resource exists
Write-Host "🔍 Verifying Application Insights resource..." -ForegroundColor Yellow
$appInsights = az monitor app-insights component show --app "$appInsightsName" --resource-group "$resourceGroupName" 2>$null
if (-not $appInsights) {
    Write-Host "❌ Application Insights resource '$appInsightsName' not found in resource group '$resourceGroupName'." -ForegroundColor Red
    exit 1
}
else {
    Write-Host "✅ Found existing Application Insights resource: $appInsightsName" -ForegroundColor Green
}

# Step 7: Build and publish the application
Write-Host "🔨 Building and publishing application..." -ForegroundColor Yellow
try {
    # Use dotnet CLI for publishing
    Write-Host "  Running publish operation..." -ForegroundColor Yellow
    dotnet clean "$PSScriptRoot\PoPunkouterSoftware\PoPunkouterSoftware.csproj"
    dotnet publish "$PSScriptRoot\PoPunkouterSoftware\PoPunkouterSoftware.csproj" --configuration Release
    
    $publishOutputPath = "$PSScriptRoot\PoPunkouterSoftware\bin\Release\net9.0\publish"
    if (-not (Test-Path $publishOutputPath)) {
        Write-Host "❌ Failed to find published output at: $publishOutputPath" -ForegroundColor Red
        exit 1
    }
    Write-Host "✅ Application published successfully to: $publishOutputPath" -ForegroundColor Green
}
catch {
    Write-Host "❌ Failed to publish application: $_" -ForegroundColor Red
    exit 1
}

# Step 8: Deploy infrastructure using Bicep
$bicepPath = "$PSScriptRoot\deploy.bicep"
Write-Host "🚀 Updating Azure infrastructure using Bicep..." -ForegroundColor Yellow
try {
    $deploymentOutput = az deployment group create `
        --resource-group "$resourceGroupName" `
        --template-file "$bicepPath" `
        --parameters webAppName="$webAppName" `
                     appInsightsName="$appInsightsName" `
                     appInsightsInstrumentationKey="$appInsightsInstrumentationKey" `
                     appInsightsAppId="$appInsightsAppId" `
                     appServicePlanName="$appServicePlanName" `
                     appServicePlanResourceGroup="$appServicePlanResourceGroup" `
                     subscriptionId="$subscriptionId" `
        --query "properties.outputs"
    
    Write-Host "✅ Azure infrastructure updated successfully" -ForegroundColor Green
}
catch {
    Write-Host "❌ Failed to update Azure infrastructure: $_" -ForegroundColor Red
    exit 1
}

# Step 9: Deploy the application to the web app
$zipPath = "$PSScriptRoot\app.zip"
Write-Host "📦 Packaging application for deployment..." -ForegroundColor Yellow
Compress-Archive -Path "$publishOutputPath\*" -DestinationPath "$zipPath" -Force

Write-Host "🚀 Deploying application to Azure Web App..." -ForegroundColor Yellow
try {
    $deployResult = az webapp deployment source config-zip `
        --resource-group "$resourceGroupName" `
        --name "$webAppName" `
        --src "$zipPath"
    
    Write-Host "✅ Application deployed successfully to web app: $webAppName" -ForegroundColor Green
}
catch {
    Write-Host "❌ Failed to deploy application: $_" -ForegroundColor Red
    exit 1
}

# Step 10: Clean up
Write-Host "🧹 Cleaning up temporary files..." -ForegroundColor Yellow
Remove-Item "$zipPath" -Force -ErrorAction SilentlyContinue

# Get the web app URL for the success message
$webAppUrl = "https://$webAppName.azurewebsites.net"

# Step 11: Provide success message and next steps
Write-Host ""
Write-Host "====================================================="
Write-Host "✅ DEPLOYMENT COMPLETED SUCCESSFULLY" -ForegroundColor Green
Write-Host "====================================================="
Write-Host ""
Write-Host "🌐 Your application is now available at: $webAppUrl" -ForegroundColor Cyan
Write-Host "📊 Application Insights is configured and collecting telemetry"
Write-Host "♻️ Your web app is now using the '$appServicePlanName' App Service Plan from resource group '$appServicePlanResourceGroup'" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next Steps:"
Write-Host "  1. Visit your application at: $webAppUrl"
Write-Host "  2. Monitor application telemetry in the Azure portal"
Write-Host "     https://portal.azure.com/#@punkouter25outlook.onmicrosoft.com/resource/subscriptions/$subscriptionId/resourceGroups/$resourceGroupName/providers/Microsoft.Insights/components/$appInsightsName/overview"
Write-Host ""
Write-Host "Telemetry data being collected includes:"
Write-Host "  - Page views"
Write-Host "  - Custom events (image loading, link clicks)"
Write-Host "  - Server requests and dependencies"
Write-Host "  - Exceptions and errors"
Write-Host ""