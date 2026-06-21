#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Probes an Azure subscription for inventory, cost, efficiency, governance, and waste signals.

.DESCRIPTION
  Uses Azure CLI to collect Azure Resource Graph inventory, Advisor recommendations,
  consumption usage, selected Monitor metrics, network/disk waste, Storage Account
  lifecycle status, Key Vault key/secret metadata, access assignments, and Azure
  OpenAI/Cognitive Services deployments. Outputs:

  - azure-inventory-report.html
  - cleanup_suggestions.ps1

  The cleanup script contains commented commands only. Review before running anything.
#>

[CmdletBinding()]
param(
    [string]$OutputDirectory = (Get-Location).Path,
    [int]$MetricLookbackDays = 30,
    [datetime]$CostStartDate,
    [datetime]$CostEndDate,
    [double]$IdleCpuThresholdPercent = 5.0
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Warn {
    param([string]$Message)
    Write-Host "WARN: $Message" -ForegroundColor Yellow
}

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-AzCliJson {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $allArgs = @($Arguments + @("--only-show-errors", "-o", "json"))
    $raw = & az @allArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) {
            Write-Warn "az $($Arguments -join ' ') failed: $raw"
            return $null
        }
        throw "az $($Arguments -join ' ') failed: $raw"
    }

    if ([string]::IsNullOrWhiteSpace(($raw | Out-String))) {
        return $null
    }

    return ($raw | Out-String | ConvertFrom-Json -Depth 100)
}

function Invoke-AzCliText {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $allArgs = @($Arguments + @("--only-show-errors"))
    $raw = & az @allArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) {
            Write-Warn "az $($Arguments -join ' ') failed: $raw"
            return $null
        }
        throw "az $($Arguments -join ' ') failed: $raw"
    }
    return ($raw | Out-String).Trim()
}

function ConvertTo-Array {
    param($Value)
    if ($null -eq $Value) { return @() }
    if ($Value -is [System.Array]) { return @($Value) }
    return @($Value)
}

function HtmlEncode {
    param($Value)
    return [System.Net.WebUtility]::HtmlEncode([string]$Value)
}

function JsString {
    param($Value)
    return ([string]$Value).Replace("\", "\\").Replace("'", "\'").Replace("`r", "").Replace("`n", " ")
}

function Get-ObjectProperty {
    param($Object, [string[]]$Names)
    foreach ($name in $Names) {
        $current = $Object
        foreach ($part in ($name -split "\.")) {
            if ($null -eq $current -or -not ($current.PSObject.Properties.Name -contains $part)) {
                $current = $null
                break
            }
            $current = $current.$part
        }
        if ($null -ne $current -and "$current" -ne "") { return $current }
    }
    return $null
}

function Get-ResourceGroupFromId {
    param([string]$ResourceId)
    if ([string]::IsNullOrWhiteSpace($ResourceId)) { return "" }
    $match = [regex]::Match($ResourceId, "/resourceGroups/([^/]+)", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) { return $match.Groups[1].Value }
    return ""
}

function Get-ResourceNameFromId {
    param([string]$ResourceId)
    if ([string]::IsNullOrWhiteSpace($ResourceId)) { return "" }
    return ($ResourceId.TrimEnd("/") -split "/")[-1]
}

function Add-CleanupCommand {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Reason,
        [string]$Command
    )
    $Lines.Add("")
    $Lines.Add("# $Reason")
    $Lines.Add("# $Command")
}

function ConvertTo-SafeDouble {
    param($Value, [double]$Default = 0)
    if ($null -eq $Value) { return $Default }
    $str = "$Value".Trim()
    if ([string]::IsNullOrEmpty($str)) { return $Default }
    if ($str -ieq "none" -or $str -ieq "null" -or $str -ieq "n/a" -or $str -ieq "-") { return $Default }
    [double]$parsed = 0
    if ([double]::TryParse($str, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        return $parsed
    }
    return $Default
}

function Get-ExpirationStatus {
    param($Expires)
    if ($null -eq $Expires) { return "No expiration" }
    $expiration = [DateTimeOffset]::FromUnixTimeSeconds([int64]$Expires)
    if ($expiration -lt [DateTimeOffset]::UtcNow) { return "Expired" }
    if ($expiration -lt [DateTimeOffset]::UtcNow.AddDays(30)) { return "Expires within 30 days" }
    return "Active"
}

if (-not (Test-Command "az")) {
    throw "Azure CLI is required. Install from https://learn.microsoft.com/cli/azure/install-azure-cli"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$reportPath = Join-Path $OutputDirectory "azure-inventory-report.html"
$cleanupPath = Join-Path $OutputDirectory "cleanup_suggestions.ps1"

Write-Step "Checking Azure CLI authentication"
$account = Invoke-AzCliJson @("account", "show") -AllowFailure
if ($null -eq $account) {
    Write-Step "Launching az login"
    & az login | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "az login failed." }
}

$subscriptions = ConvertTo-Array (Invoke-AzCliJson @("account", "list", "--all")) |
    Where-Object { $_.state -eq "Enabled" } |
    Sort-Object name

if ($subscriptions.Count -eq 0) {
    throw "No enabled Azure subscriptions are available for this identity."
}

$current = Invoke-AzCliJson @("account", "show")
if ($subscriptions.Count -eq 1) {
    $selected = $subscriptions[0]
}
else {
    Write-Host ""
    Write-Host "Available subscriptions:" -ForegroundColor Green
    for ($i = 0; $i -lt $subscriptions.Count; $i++) {
        $marker = if ($subscriptions[$i].id -eq $current.id) { "*" } else { " " }
        Write-Host ("[{0}] {1} {2} ({3})" -f $i, $marker, $subscriptions[$i].name, $subscriptions[$i].id)
    }
    $choice = Read-Host "Select subscription index, or press Enter for current '$($current.name)'"
    if ([string]::IsNullOrWhiteSpace($choice)) {
        $selected = $subscriptions | Where-Object { $_.id -eq $current.id } | Select-Object -First 1
    }
    else {
        $selected = $subscriptions[[int]$choice]
    }
}

Invoke-AzCliText @("account", "set", "--subscription", $selected.id) | Out-Null
$subscriptionId = $selected.id
$subscriptionName = $selected.name
Write-Step "Using subscription '$subscriptionName' ($subscriptionId)"

Write-Step "Ensuring Resource Graph extension is installed"
$extensions = ConvertTo-Array (Invoke-AzCliJson @("extension", "list") -AllowFailure)
if (-not ($extensions | Where-Object { $_.name -eq "resource-graph" })) {
    & az extension add --name resource-graph --only-show-errors | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Failed to install Azure Resource Graph extension." }
}

$metricEndDate = (Get-Date).Date
$metricStartDate = $metricEndDate.AddDays(-1 * $MetricLookbackDays)
$timespan = "{0}/{1}" -f $metricStartDate.ToUniversalTime().ToString("o"), $metricEndDate.ToUniversalTime().ToString("o")

if (-not $PSBoundParameters.ContainsKey("CostStartDate")) {
    $currentMonthStart = Get-Date -Day 1 -Hour 0 -Minute 0 -Second 0 -Millisecond 0
    $CostStartDate = $currentMonthStart.AddMonths(-1)
}
if (-not $PSBoundParameters.ContainsKey("CostEndDate")) {
    $CostEndDate = Get-Date -Day 1 -Hour 0 -Minute 0 -Second 0 -Millisecond 0
}
$costWindowLabel = "{0} to {1}" -f $CostStartDate.ToString("yyyy-MM-dd"), $CostEndDate.ToString("yyyy-MM-dd")

Write-Step "Collecting Resource Graph inventory"
$resourceQuery = @"
Resources
| project id, name, type, resourceGroup, location, subscriptionId, sku=tostring(sku.name), kind=tostring(kind), tags
| order by resourceGroup asc, type asc, name asc
"@

$resourceList = [System.Collections.Generic.List[object]]::new()
$skipToken = $null
do {
    $graphArguments = @("graph", "query", "-q", $resourceQuery, "--subscriptions", $subscriptionId, "--first", "1000")
    if (-not [string]::IsNullOrWhiteSpace($skipToken)) {
        $graphArguments += @("--skip-token", $skipToken)
    }

    $resourceGraph = Invoke-AzCliJson $graphArguments
    foreach ($resource in (ConvertTo-Array $resourceGraph.data)) {
        $resourceList.Add($resource)
    }
    $skipToken = Get-ObjectProperty $resourceGraph @("skip_token", "skipToken")
} while (-not [string]::IsNullOrWhiteSpace($skipToken))
$resources = @($resourceList)

# Resource Graph returns resources, not empty resource groups. Query groups separately
# so the report remains a complete subscription matrix even when a group has no services.
$azureResourceGroups = ConvertTo-Array (Invoke-AzCliJson @("group", "list"))
$resourceGroups = $azureResourceGroups |
    ForEach-Object {
        $groupName = $_.name
        $groupResources = @($resources | Where-Object { $_.resourceGroup -ieq $groupName })
        [pscustomobject]@{
            Name = $groupName
            ResourceCount = $groupResources.Count
            Resources = $groupResources
        }
    } |
    Sort-Object Name

Write-Step "Collecting Azure Advisor cost and performance recommendations"
$advisorRecommendations = @()
foreach ($category in @("Cost", "Performance")) {
    $items = ConvertTo-Array (Invoke-AzCliJson @("advisor", "recommendation", "list", "--category", $category) -AllowFailure)
    foreach ($item in $items) {
        $resourceId = Get-ObjectProperty $item @("resourceMetadata.resourceId", "impactedValue", "id")
        if ($item.resourceMetadata -and $item.resourceMetadata.resourceId) { $resourceId = $item.resourceMetadata.resourceId }
        $advisorRecommendations += [pscustomobject]@{
            Category = $category
            ResourceId = $resourceId
            ResourceGroup = Get-ResourceGroupFromId $resourceId
            Impact = Get-ObjectProperty $item @("impact")
            Problem = Get-ObjectProperty $item @("shortDescription.problem", "problem")
            Solution = Get-ObjectProperty $item @("shortDescription.solution", "solution")
            SavingsAmount = Get-ObjectProperty $item @("extendedProperties.savingsAmount", "savingsAmount")
            SavingsCurrency = Get-ObjectProperty $item @("extendedProperties.savingsCurrency", "savingsCurrency")
        }
    }
}

Write-Step "Collecting consumption usage for $costWindowLabel"
$usage = ConvertTo-Array (Invoke-AzCliJson @(
        "consumption", "usage", "list",
        "--start-date", $CostStartDate.ToString("yyyy-MM-dd"),
        "--end-date", $CostEndDate.ToString("yyyy-MM-dd")
    ) -AllowFailure)

$spendByResourceGroup = @{}
foreach ($row in $usage) {
    $rg = Get-ObjectProperty $row @("resourceGroup", "resourceGroupName")
    if ([string]::IsNullOrWhiteSpace($rg)) {
        $instanceId = Get-ObjectProperty $row @("instanceId", "resourceId")
        $rg = Get-ResourceGroupFromId $instanceId
    }
    if ([string]::IsNullOrWhiteSpace($rg)) { $rg = "(unassigned)" }
    $cost = Get-ObjectProperty $row @("pretaxCost", "cost", "billingPreTaxTotal")
    $cost = ConvertTo-SafeDouble $cost
    if (-not $spendByResourceGroup.ContainsKey($rg)) { $spendByResourceGroup[$rg] = 0.0 }
    $spendByResourceGroup[$rg] += $cost
}

Write-Step "Detecting orphaned public IPs, NICs, and disks"
$publicIps = $resources | Where-Object { $_.type -ieq "microsoft.network/publicipaddresses" }
$nics = $resources | Where-Object { $_.type -ieq "microsoft.network/networkinterfaces" }
$disks = $resources | Where-Object { $_.type -ieq "microsoft.compute/disks" }

$wasteItems = @()
$cleanupLines = [System.Collections.Generic.List[string]]::new()
$cleanupLines.Add("# Generated by New-AzureEfficiencyReport.ps1 on $(Get-Date -Format o)")
$cleanupLines.Add("# Commands are intentionally commented out. Review each line before uncommenting.")
$cleanupLines.Add("# Subscription: $subscriptionName ($subscriptionId)")

foreach ($pip in $publicIps) {
    $detail = Invoke-AzCliJson @("network", "public-ip", "show", "--ids", $pip.id) -AllowFailure
    if ($detail -and $null -eq $detail.ipConfiguration) {
        $wasteItems += [pscustomobject]@{ Type = "Unassociated Public IP"; ResourceGroup = $pip.resourceGroup; Name = $pip.name; ResourceId = $pip.id; Severity = "High" }
        Add-CleanupCommand $cleanupLines "Delete unassociated Public IP '$($pip.name)' in '$($pip.resourceGroup)'." "az network public-ip delete --ids '$($pip.id)'"
    }
}

foreach ($nic in $nics) {
    $detail = Invoke-AzCliJson @("network", "nic", "show", "--ids", $nic.id) -AllowFailure
    if ($detail -and $null -eq $detail.virtualMachine) {
        $wasteItems += [pscustomobject]@{ Type = "Orphaned NIC"; ResourceGroup = $nic.resourceGroup; Name = $nic.name; ResourceId = $nic.id; Severity = "Medium" }
        Add-CleanupCommand $cleanupLines "Delete orphaned NIC '$($nic.name)' in '$($nic.resourceGroup)'." "az network nic delete --ids '$($nic.id)'"
    }
}

foreach ($disk in $disks) {
    $detail = Invoke-AzCliJson @("disk", "show", "--ids", $disk.id) -AllowFailure
    if ($detail -and ($detail.diskState -match "Unattached|Reserved") -and $null -eq $detail.managedBy) {
        $wasteItems += [pscustomobject]@{ Type = "Detached Managed Disk"; ResourceGroup = $disk.resourceGroup; Name = $disk.name; ResourceId = $disk.id; Severity = "High" }
        Add-CleanupCommand $cleanupLines "Delete detached disk '$($disk.name)' in '$($disk.resourceGroup)'." "az disk delete --ids '$($disk.id)' --yes"
    }
}

function Get-MetricSummary {
    param(
        [string]$ResourceId,
        [string[]]$MetricNames,
        [string]$Aggregation = "Average"
    )

    $results = @{}
    foreach ($metric in $MetricNames) {
        $metricResult = Invoke-AzCliJson @(
            "monitor", "metrics", "list",
            "--resource", $ResourceId,
            "--metrics", $metric,
            "--start-time", $metricStartDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            "--end-time", $metricEndDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            "--interval", "PT24H",
            "--aggregation", $Aggregation
        ) -AllowFailure

        $values = @()
        foreach ($metricItem in (ConvertTo-Array $metricResult.value)) {
            foreach ($series in (ConvertTo-Array $metricItem.timeseries)) {
                foreach ($point in (ConvertTo-Array $series.data)) {
                    $value = if ($Aggregation -eq "Total") { $point.total } else { $point.average }
                    if ($null -ne $value) { $values += [double]$value }
                }
            }
        }

        if ($values.Count -gt 0) {
            $results[$metric] = [pscustomobject]@{
                Average = [math]::Round((($values | Measure-Object -Average).Average), 2)
                Total = [math]::Round((($values | Measure-Object -Sum).Sum), 2)
                Points = $values.Count
            }
        }
    }
    return $results
}

Write-Step "Evaluating App Service and Function efficiency metrics"
$efficiencyItems = @()
$webResources = $resources | Where-Object {
    $_.type -ieq "microsoft.web/serverfarms" -or $_.type -ieq "microsoft.web/sites"
}

foreach ($resource in $webResources) {
    if ($resource.type -ieq "microsoft.web/serverfarms") {
        $metrics = Get-MetricSummary -ResourceId $resource.id -MetricNames @("CpuPercentage") -Aggregation "Average"
        $cpu = if ($metrics.ContainsKey("CpuPercentage")) { $metrics["CpuPercentage"].Average } else { $null }
        if ($null -ne $cpu -and $cpu -lt $IdleCpuThresholdPercent) {
            $efficiencyItems += [pscustomobject]@{
                Type = "Low App Service Plan CPU"
                ResourceGroup = $resource.resourceGroup
                Name = $resource.name
                ResourceId = $resource.id
                Detail = "Average CPU $cpu% over $MetricLookbackDays days"
            }
            Add-CleanupCommand $cleanupLines "Review downscale for App Service Plan '$($resource.name)' with average CPU $cpu%." "az appservice plan update --ids '$($resource.id)' --sku B1"
        }
    }
    else {
        $cpuMetrics = Get-MetricSummary -ResourceId $resource.id -MetricNames @("CpuTime") -Aggregation "Total"
        $trafficMetrics = @("Requests")
        if ($resource.kind -match "functionapp") {
            $trafficMetrics += "FunctionExecutionCount"
        }
        $requestMetrics = Get-MetricSummary -ResourceId $resource.id -MetricNames $trafficMetrics -Aggregation "Total"
        $requestTotal = 0.0
        foreach ($key in $requestMetrics.Keys) { $requestTotal += [double]$requestMetrics[$key].Total }
        $cpuTotal = if ($cpuMetrics.ContainsKey("CpuTime")) { $cpuMetrics["CpuTime"].Total } else { $null }

        if ($requestTotal -eq 0 -and $null -ne $cpuTotal) {
            $efficiencyItems += [pscustomobject]@{
                Type = "Zero Traffic App/Function"
                ResourceGroup = $resource.resourceGroup
                Name = $resource.name
                ResourceId = $resource.id
                Detail = "No requests/executions detected over $MetricLookbackDays days"
            }
            Add-CleanupCommand $cleanupLines "Stop zero-traffic Web App/Function '$($resource.name)'." "az webapp stop --ids '$($resource.id)'"
        }
    }
}

Write-Step "Auditing Storage Account tiers and lifecycle policies"
$storageAudits = @()
$storageAccounts = $resources | Where-Object { $_.type -ieq "microsoft.storage/storageaccounts" }
foreach ($storage in $storageAccounts) {
    $account = Invoke-AzCliJson @("storage", "account", "show", "--ids", $storage.id) -AllowFailure
    $lifecycle = Invoke-AzCliJson @("storage", "account", "management-policy", "show", "--account-name", $storage.name, "--resource-group", $storage.resourceGroup) -AllowFailure
    $storageAudits += [pscustomobject]@{
        ResourceGroup = $storage.resourceGroup
        Name = $storage.name
        Sku = $account.sku.name
        AccessTier = if ($account.accessTier) { $account.accessTier } else { "n/a" }
        Kind = $account.kind
        LifecyclePolicyActive = [bool]($lifecycle -and $lifecycle.policy.rules.Count -gt 0)
        RuleCount = if ($lifecycle -and $lifecycle.policy.rules) { $lifecycle.policy.rules.Count } else { 0 }
        ResourceId = $storage.id
    }
}

Write-Step "Inspecting Key Vault keys, secrets, and access"
$keyVaultInventories = @()
$keyVaults = $resources | Where-Object { $_.type -ieq "microsoft.keyvault/vaults" }
foreach ($vault in $keyVaults) {
    $keys = ConvertTo-Array (Invoke-AzCliJson @("keyvault", "key", "list", "--vault-name", $vault.name) -AllowFailure)
    $secrets = ConvertTo-Array (Invoke-AzCliJson @("keyvault", "secret", "list", "--vault-name", $vault.name) -AllowFailure)
    $vaultDetail = Invoke-AzCliJson @("keyvault", "show", "--name", $vault.name, "--resource-group", $vault.resourceGroup) -AllowFailure
    $roleAssignments = ConvertTo-Array (Invoke-AzCliJson @("role", "assignment", "list", "--scope", $vault.id) -AllowFailure)

    $keyVaultInventories += [pscustomobject]@{
        ResourceGroup = $vault.resourceGroup
        Name = $vault.name
        ResourceId = $vault.id
        EnableRbacAuthorization = if ($vaultDetail) { [bool]$vaultDetail.properties.enableRbacAuthorization } else { $false }
        AccessPolicies = if ($vaultDetail) { ConvertTo-Array $vaultDetail.properties.accessPolicies } else { @() }
        RoleAssignments = $roleAssignments
        Keys = $keys | ForEach-Object {
            [pscustomobject]@{
                Name = $_.name
                Created = if ($_.attributes.created) { ([DateTimeOffset]::FromUnixTimeSeconds([int64]$_.attributes.created)).DateTime.ToString("yyyy-MM-dd") } else { "" }
                Expires = if ($_.attributes.expires) { ([DateTimeOffset]::FromUnixTimeSeconds([int64]$_.attributes.expires)).DateTime.ToString("yyyy-MM-dd") } else { "No expiration" }
                ExpirationStatus = Get-ExpirationStatus $_.attributes.expires
                Enabled = $_.attributes.enabled
            }
        }
        Secrets = $secrets | ForEach-Object {
            [pscustomobject]@{
                Name = $_.name
                Created = if ($_.attributes.created) { ([DateTimeOffset]::FromUnixTimeSeconds([int64]$_.attributes.created)).DateTime.ToString("yyyy-MM-dd") } else { "" }
                Expires = if ($_.attributes.expires) { ([DateTimeOffset]::FromUnixTimeSeconds([int64]$_.attributes.expires)).DateTime.ToString("yyyy-MM-dd") } else { "No expiration" }
                ExpirationStatus = Get-ExpirationStatus $_.attributes.expires
                Enabled = $_.attributes.enabled
            }
        }
    }
}

Write-Step "Inspecting Azure OpenAI / Cognitive Services deployments"
$aiDeployments = @()
$aiAccounts = $resources | Where-Object {
    $_.type -ieq "microsoft.cognitiveservices/accounts" -and
    ($_.kind -match "OpenAI|AIServices|CognitiveServices" -or $_.name -match "openai|ai")
}
foreach ($accountResource in $aiAccounts) {
    $deployments = ConvertTo-Array (Invoke-AzCliJson @(
        "cognitiveservices", "account", "deployment", "list",
        "--name", $accountResource.name,
        "--resource-group", $accountResource.resourceGroup
    ) -AllowFailure)

    foreach ($deployment in $deployments) {
        $aiDeployments += [pscustomobject]@{
            ResourceGroup = $accountResource.resourceGroup
            AccountName = $accountResource.name
            DeploymentName = $deployment.name
            ModelName = $deployment.properties.model.name
            ModelVersion = $deployment.properties.model.version
            Format = $deployment.properties.model.format
            ScaleType = $deployment.sku.name
            Capacity = $deployment.sku.capacity
            ResourceId = $accountResource.id
        }
    }
}

$foundryHubs = $resources | Where-Object {
    $_.type -ieq "microsoft.machinelearningservices/workspaces" -and
    ($_.kind -match "hub|project" -or $_.name -match "hub|foundry|ai")
}
if ($foundryHubs.Count -gt 0) {
    $mlExtension = ConvertTo-Array (Invoke-AzCliJson @("extension", "list") -AllowFailure) |
        Where-Object { $_.name -eq "ml" } |
        Select-Object -First 1

    if ($null -eq $mlExtension) {
        Write-Warn "Azure ML extension is not installed. Foundry hub online endpoint deployment probing will be skipped. Install with: az extension add -n ml"
    }

    foreach ($hub in $foundryHubs) {
        if ($null -eq $mlExtension) {
            $aiDeployments += [pscustomobject]@{
                ResourceGroup = $hub.resourceGroup
                AccountName = $hub.name
                DeploymentName = "Azure ML extension missing"
                ModelName = "Foundry hub detected"
                ModelVersion = ""
                Format = "Microsoft.MachineLearningServices/workspaces"
                ScaleType = "n/a"
                Capacity = ""
                ResourceId = $hub.id
            }
            continue
        }

        $endpoints = ConvertTo-Array (Invoke-AzCliJson @(
            "ml", "online-endpoint", "list",
            "--workspace-name", $hub.name,
            "--resource-group", $hub.resourceGroup
        ) -AllowFailure)

        foreach ($endpoint in $endpoints) {
            $deployments = ConvertTo-Array (Invoke-AzCliJson @(
                "ml", "online-deployment", "list",
                "--endpoint-name", $endpoint.name,
                "--workspace-name", $hub.name,
                "--resource-group", $hub.resourceGroup
            ) -AllowFailure)

            foreach ($deployment in $deployments) {
                $aiDeployments += [pscustomobject]@{
                    ResourceGroup = $hub.resourceGroup
                    AccountName = $hub.name
                    DeploymentName = "$($endpoint.name)/$($deployment.name)"
                    ModelName = Get-ObjectProperty $deployment @("model", "properties.model")
                    ModelVersion = Get-ObjectProperty $deployment @("modelVersion", "properties.modelVersion")
                    Format = "Azure ML online deployment"
                    ScaleType = Get-ObjectProperty $deployment @("sku.name", "instance_type")
                    Capacity = Get-ObjectProperty $deployment @("sku.capacity", "instance_count")
                    ResourceId = $hub.id
                }
            }
        }
    }
}

$allFindings = @($advisorRecommendations + $wasteItems + $efficiencyItems)
$totalSpend = 0.0
foreach ($value in $spendByResourceGroup.Values) { $totalSpend += ConvertTo-SafeDouble $value }

Write-Step "Rendering HTML report"

$rgOptions = ($resourceGroups | ForEach-Object {
    "<option value='$(HtmlEncode $_.Name)'>$(HtmlEncode $_.Name)</option>"
}) -join "`n"

$groupSections = foreach ($group in $resourceGroups) {
    $rgName = $group.Name
    $rgResources = @($group.Resources)
    $rgSpend = if ($spendByResourceGroup.ContainsKey($rgName)) { [double]$spendByResourceGroup[$rgName] } else { 0.0 }
    $rgAdvisor = @($advisorRecommendations | Where-Object { $_.ResourceGroup -ieq $rgName })
    $rgWaste = @($wasteItems | Where-Object { $_.ResourceGroup -ieq $rgName })
    $rgEfficiency = @($efficiencyItems | Where-Object { $_.ResourceGroup -ieq $rgName })
    $rgStorage = @($storageAudits | Where-Object { $_.ResourceGroup -ieq $rgName })
    $rgVaults = @($keyVaultInventories | Where-Object { $_.ResourceGroup -ieq $rgName })
    $rgAi = @($aiDeployments | Where-Object { $_.ResourceGroup -ieq $rgName })

    $resourceRows = ($rgResources | ForEach-Object {
        "<tr><td class='py-2 pr-4 font-medium'>$(HtmlEncode $_.name)</td><td class='py-2 pr-4 text-slate-600'>$(HtmlEncode $_.type)</td><td class='py-2 pr-4'>$(HtmlEncode $_.location)</td><td class='py-2'>$(HtmlEncode $_.sku)</td></tr>"
    }) -join "`n"

    $findingCards = @()
    foreach ($rec in $rgAdvisor) {
        $savings = if ($rec.SavingsAmount) { "$($rec.SavingsAmount) $($rec.SavingsCurrency)" } else { "n/a" }
        $findingCards += "<li class='rounded-md border border-amber-200 bg-amber-50 p-3'><div class='font-semibold'>$(HtmlEncode $rec.Category) Advisor: $(HtmlEncode $rec.Impact)</div><div>$(HtmlEncode $rec.Problem)</div><div class='text-slate-600'>$(HtmlEncode $rec.Solution)</div><div class='text-xs text-amber-700'>Savings: $(HtmlEncode $savings)</div></li>"
    }
    foreach ($item in $rgWaste) {
        $findingCards += "<li class='rounded-md border border-red-200 bg-red-50 p-3'><div class='font-semibold'>$(HtmlEncode $item.Type)</div><div>$(HtmlEncode $item.Name)</div><div class='break-all text-xs text-slate-600'>$(HtmlEncode $item.ResourceId)</div></li>"
    }
    foreach ($item in $rgEfficiency) {
        $findingCards += "<li class='rounded-md border border-blue-200 bg-blue-50 p-3'><div class='font-semibold'>$(HtmlEncode $item.Type)</div><div>$(HtmlEncode $item.Name)</div><div class='text-slate-600'>$(HtmlEncode $item.Detail)</div></li>"
    }
    $findingsHtml = if ($findingCards.Count -gt 0) { "<ul class='grid gap-3 md:grid-cols-2'>$($findingCards -join "`n")</ul>" } else { "<p class='text-sm text-slate-500'>No Advisor, orphaned resource, or low-utilization findings mapped to this group.</p>" }

    $storageHtml = if ($rgStorage.Count -gt 0) {
        "<div class='mt-5'><h4 class='mb-2 font-semibold'>Storage tier audit</h4><div class='grid gap-2 md:grid-cols-2'>" + (($rgStorage | ForEach-Object {
            $policy = if ($_.LifecyclePolicyActive) { "Lifecycle active ($($_.RuleCount) rules)" } else { "No lifecycle policy" }
            "<div class='rounded-md border p-3'><div class='font-medium'>$(HtmlEncode $_.Name)</div><div class='text-sm text-slate-600'>SKU $(HtmlEncode $_.Sku), tier $(HtmlEncode $_.AccessTier), kind $(HtmlEncode $_.Kind)</div><div class='text-sm'>$(HtmlEncode $policy)</div></div>"
        }) -join "`n") + "</div></div>"
    } else { "" }

    $vaultHtml = if ($rgVaults.Count -gt 0) {
        "<div class='mt-5'><h4 class='mb-2 font-semibold'>Key Vault inventories</h4>" + (($rgVaults | ForEach-Object {
            $keyRows = if ($_.Keys.Count -gt 0) { ($_.Keys | ForEach-Object { "<li>$(HtmlEncode $_.Name) <span class='text-slate-500'>created $(HtmlEncode $_.Created), expires $(HtmlEncode $_.Expires), status $(HtmlEncode $_.ExpirationStatus), enabled $(HtmlEncode $_.Enabled)</span></li>" }) -join "" } else { "<li class='text-slate-500'>No keys listed or access denied.</li>" }
            $secretRows = if ($_.Secrets.Count -gt 0) { ($_.Secrets | ForEach-Object { "<li>$(HtmlEncode $_.Name) <span class='text-slate-500'>created $(HtmlEncode $_.Created), expires $(HtmlEncode $_.Expires), status $(HtmlEncode $_.ExpirationStatus), enabled $(HtmlEncode $_.Enabled)</span></li>" }) -join "" } else { "<li class='text-slate-500'>No secrets listed or access denied.</li>" }
            $rbacRows = if ($_.RoleAssignments.Count -gt 0) { ($_.RoleAssignments | Select-Object -First 20 | ForEach-Object { "<li>$(HtmlEncode $_.principalName) <span class='text-slate-500'>$(HtmlEncode $_.roleDefinitionName)</span></li>" }) -join "" } else { "" }
            $policyRows = if ($_.AccessPolicies.Count -gt 0) { ($_.AccessPolicies | ForEach-Object { "<li>Tenant $(HtmlEncode $_.tenantId), Object $(HtmlEncode $_.objectId)</li>" }) -join "" } else { "" }
            "<div class='mb-3 rounded-md border p-3'><div class='font-medium'>$(HtmlEncode $_.Name)</div><div class='mt-2 grid gap-3 md:grid-cols-3'><div><div class='text-xs uppercase text-slate-500'>Keys</div><ul class='text-sm'>$keyRows</ul></div><div><div class='text-xs uppercase text-slate-500'>Secrets</div><ul class='text-sm'>$secretRows</ul></div><div><div class='text-xs uppercase text-slate-500'>Access</div><ul class='text-sm'>$rbacRows$policyRows</ul></div></div></div>"
        }) -join "`n") + "</div>"
    } else { "" }

    $aiHtml = if ($rgAi.Count -gt 0) {
        "<div class='mt-5'><h4 class='mb-2 font-semibold'>Azure AI / OpenAI deployments</h4><div class='grid gap-2 md:grid-cols-2'>" + (($rgAi | ForEach-Object {
            "<div class='rounded-md border p-3'><div class='font-medium'>$(HtmlEncode $_.DeploymentName)</div><div class='text-sm text-slate-600'>$(HtmlEncode $_.AccountName): $(HtmlEncode $_.ModelName) $(HtmlEncode $_.ModelVersion)</div><div class='text-sm'>Type $(HtmlEncode $_.ScaleType), capacity $(HtmlEncode $_.Capacity), format $(HtmlEncode $_.Format)</div></div>"
        }) -join "`n") + "</div></div>"
    } else { "" }

@"
<section class="rg-section rounded-lg border border-slate-200 bg-white p-5 shadow-sm" data-rg="$(HtmlEncode $rgName)" data-search="$(HtmlEncode ($rgName + ' ' + (($rgResources | ForEach-Object { "$($_.name) $($_.type)" }) -join ' ')))">
  <div class="mb-4 flex flex-col gap-2 md:flex-row md:items-start md:justify-between">
    <div>
      <h3 class="text-xl font-bold text-slate-900">$(HtmlEncode $rgName)</h3>
      <p class="text-sm text-slate-500">$($rgResources.Count) resources · $($rgAdvisor.Count + $rgWaste.Count + $rgEfficiency.Count) findings</p>
    </div>
    <div class="rounded-md bg-slate-100 px-3 py-2 text-right">
      <div class="text-xs uppercase text-slate-500">Billing cycle spend</div>
      <div class="text-lg font-bold">$(("{0:C2}" -f $rgSpend))</div>
    </div>
  </div>
  <div class="mb-5 overflow-x-auto">
    <table class="min-w-full text-left text-sm">
      <thead class="border-b text-xs uppercase text-slate-500"><tr><th class="py-2 pr-4">Name</th><th class="py-2 pr-4">Type</th><th class="py-2 pr-4">Location</th><th class="py-2">SKU</th></tr></thead>
      <tbody class="divide-y">$resourceRows</tbody>
    </table>
  </div>
  <h4 class="mb-2 font-semibold">Efficiency and waste findings</h4>
  $findingsHtml
  $storageHtml
  $vaultHtml
  $aiHtml
</section>
"@
}

$csvRows = foreach ($group in $resourceGroups) {
    $rgName = $group.Name
    foreach ($resource in $group.Resources) {
        [pscustomobject]@{
            ResourceGroup = $rgName
            Name = $resource.name
            Type = $resource.type
            Location = $resource.location
            Sku = $resource.sku
            SpendLastDays = if ($spendByResourceGroup.ContainsKey($rgName)) { [math]::Round([double]$spendByResourceGroup[$rgName], 2) } else { 0 }
        }
    }
}
$csvJson = ($csvRows | ConvertTo-Json -Depth 10 -Compress) `
    -replace '<', '\u003c' `
    -replace '>', '\u003e' `
    -replace '&', '\u0026'

$html = @"
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Azure Inventory Efficiency Report</title>
  <script src="https://cdn.tailwindcss.com"></script>
</head>
<body class="bg-slate-50 text-slate-800">
  <header class="border-b bg-white">
    <div class="mx-auto max-w-7xl px-4 py-8">
      <div class="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
        <div>
          <p class="text-sm font-semibold uppercase tracking-wide text-blue-700">Azure Subscription Efficiency Report</p>
          <h1 class="mt-1 text-3xl font-black text-slate-950">$(HtmlEncode $subscriptionName)</h1>
          <p class="mt-2 text-sm text-slate-500">$(HtmlEncode $subscriptionId) · Generated $(Get-Date -Format "yyyy-MM-dd HH:mm") · Spend window $costWindowLabel · Metrics window $MetricLookbackDays days</p>
        </div>
        <button id="exportCsv" class="rounded-md bg-blue-700 px-4 py-2 text-sm font-semibold text-white shadow-sm hover:bg-blue-800">Export to CSV</button>
      </div>
    </div>
  </header>

  <main class="mx-auto max-w-7xl px-4 py-6">
    <section class="grid gap-4 md:grid-cols-4">
      <div class="rounded-lg border bg-white p-4 shadow-sm"><div class="text-xs uppercase text-slate-500">Resource groups</div><div class="mt-1 text-3xl font-black">$($resourceGroups.Count)</div></div>
      <div class="rounded-lg border bg-white p-4 shadow-sm"><div class="text-xs uppercase text-slate-500">Total services</div><div class="mt-1 text-3xl font-black">$($resources.Count)</div></div>
      <div class="rounded-lg border bg-white p-4 shadow-sm"><div class="text-xs uppercase text-slate-500">Cost recommendations</div><div class="mt-1 text-3xl font-black">$(@($advisorRecommendations | Where-Object { $_.Category -eq "Cost" }).Count)</div></div>
      <div class="rounded-lg border bg-white p-4 shadow-sm"><div class="text-xs uppercase text-slate-500">Billing cycle spend</div><div class="mt-1 text-3xl font-black">$(("{0:C2}" -f $totalSpend))</div></div>
    </section>

    <section class="sticky top-0 z-10 my-6 rounded-lg border bg-white/95 p-4 shadow-sm backdrop-blur">
      <div class="grid gap-3 md:grid-cols-[1fr_260px]">
        <input id="searchBox" class="rounded-md border border-slate-300 px-3 py-2" placeholder="Search resources, types, groups, findings...">
        <select id="rgFilter" class="rounded-md border border-slate-300 px-3 py-2">
          <option value="">All resource groups</option>
          $rgOptions
        </select>
      </div>
    </section>

    <section id="groups" class="space-y-5">
      $($groupSections -join "`n")
    </section>
  </main>

  <script>
    const csvRows = $csvJson;
    const searchBox = document.getElementById('searchBox');
    const rgFilter = document.getElementById('rgFilter');
    const sections = [...document.querySelectorAll('.rg-section')];

    function applyFilters() {
      const q = searchBox.value.trim().toLowerCase();
      const rg = rgFilter.value;
      for (const section of sections) {
        const matchesRg = !rg || section.dataset.rg === rg;
        const matchesText = !q || section.innerText.toLowerCase().includes(q) || section.dataset.search.toLowerCase().includes(q);
        section.classList.toggle('hidden', !(matchesRg && matchesText));
      }
    }

    searchBox.addEventListener('input', applyFilters);
    rgFilter.addEventListener('change', applyFilters);

    document.getElementById('exportCsv').addEventListener('click', () => {
      const headers = Object.keys(csvRows[0] || {});
      const lines = [headers.join(',')];
      for (const row of csvRows) {
        lines.push(headers.map(h => '"' + String(row[h] ?? '').replaceAll('"', '""') + '"').join(','));
      }
      const blob = new Blob([lines.join('\n')], { type: 'text/csv' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'azure-inventory-report.csv';
      a.click();
      URL.revokeObjectURL(url);
    });
  </script>
</body>
</html>
"@

Set-Content -Path $reportPath -Value $html -Encoding UTF8
Set-Content -Path $cleanupPath -Value ($cleanupLines -join [Environment]::NewLine) -Encoding UTF8

Write-Step "Done"
Write-Host "Report:  $reportPath" -ForegroundColor Green
Write-Host "Cleanup: $cleanupPath" -ForegroundColor Green
