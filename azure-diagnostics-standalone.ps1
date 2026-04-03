<#
.SYNOPSIS
    Standalone Azure diagnostics script — generates a local HTML report analysing
    your Azure subscription.  No web app required.

.DESCRIPTION
    Uses the 'az' CLI (already installed when you work with Azure locally).
    Run once to get an HTML snapshot of all your web services, costs, SSL certs,
    config issues and free-tier opportunities. Optionally uploads the JSON report
    to Azure Table Storage so your deployed PoPunkouterSoftware dashboard picks it up.

.PARAMETER SubscriptionId
    Override the default subscription.  If omitted, uses the az CLI default.

.PARAMETER OutputDir
    Folder to write the HTML (and JSON) report.  Default: current directory.

.PARAMETER UploadToTableStorage
    Upload the JSON report to Azure Table Storage so the web app can read it.
    Requires -StorageAccountName or -TableStorageConnectionString.

.PARAMETER StorageAccountName
    Storage account to upload to (uses az CLI Managed Identity / current login).

.PARAMETER TableStorageConnectionString
    Full connection string for Table Storage (alternative to -StorageAccountName).

.EXAMPLE
    # Basic local report
    .\azure-diagnostics-standalone.ps1

.EXAMPLE
    # Local report + upload to Table Storage so the web app dashboard updates
    .\azure-diagnostics-standalone.ps1 -UploadToTableStorage -StorageAccountName mystorageaccount

.NOTES
    Prerequisites: az CLI logged in ('az login' or service principal env vars).
    Install az CLI: https://docs.microsoft.com/cli/azure/install-azure-cli
#>
[CmdletBinding()]
param(
    [string] $SubscriptionId              = "",
    [string] $OutputDir                   = ".",
    [switch] $UploadToTableStorage,
    [string] $StorageAccountName          = "",
    [string] $TableStorageConnectionString = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Helpers ────────────────────────────────────────────────────────────────────

function Write-Step([string]$msg) { Write-Host "  → $msg" -ForegroundColor Cyan }
function Write-Ok  ([string]$msg) { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "  ⚠ $msg" -ForegroundColor Yellow }

function Invoke-Az([string[]]$args) {
    $result = az @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed: $result" }
    return $result | ConvertFrom-Json
}

function Invoke-AzRaw([string[]]$args) {
    $result = az @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($args -join ' ') failed: $result" }
    return ($result -join "") | ConvertFrom-Json
}

function Test-Url([string]$url) {
    try {
        $sw  = [System.Diagnostics.Stopwatch]::StartNew()
        $req = [System.Net.WebRequest]::Create($url)
        $req.Method  = "HEAD"
        $req.Timeout = 12000
        $req.AllowAutoRedirect = $false
        try { $resp = $req.GetResponse(); $code = [int]$resp.StatusCode; $resp.Close() }
        catch [System.Net.WebException] { $code = [int]$_.Exception.Response.StatusCode }
        $sw.Stop()
        $success = $code -ge 200 -and $code -lt 400
        return @{ success = $success; statusCode = $code; responseTime = $sw.ElapsedMilliseconds }
    } catch {
        return @{ success = $false; statusCode = 0; responseTime = 0; error = $_.Exception.Message }
    }
}

function Get-SslExpiry([string]$hostname) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient($hostname, 443)
        $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false,
            { param($s,$c,$ch,$e) $true })
        $ssl.AuthenticateAsClient($hostname)
        $cert    = $ssl.RemoteCertificate
        $expiry  = [datetime]::Parse($cert.GetExpirationDateString())
        $daysLeft = ($expiry - [datetime]::UtcNow).Days
        $ssl.Close(); $tcp.Close()
        return @{ expiry = $expiry.ToString("yyyy-MM-dd"); daysLeft = $daysLeft; subject = $cert.Subject }
    } catch {
        return @{ error = $_.Exception.Message }
    }
}

# ── Free-tier knowledge base (mirrors AzureReportService.cs) ──────────────────

$FreeTierMap = @{
    "Microsoft.Web/sites"                      = @{ label="App Service";         freeSku="F1";   note="F1: 60 CPU-min/day" }
    "Microsoft.Web/serverFarms"                = @{ label="App Service Plan";    freeSku="F1";   note="Downgrade to F1 if traffic is low" }
    "Microsoft.Web/staticSites"                = @{ label="Static Web App";      freeSku="Free"; note="Free: 100 GB bandwidth/month" }
    "Microsoft.App/containerApps"             = @{ label="Container App";        freeSku=$null;  note="Set min-replicas=0 to stay in free quota" }
    "Microsoft.ContainerRegistry/registries"   = @{ label="Container Registry";  freeSku=$null;  note="Basic ~`$5/mo. ghcr.io is free for private images" }
    "Microsoft.DocumentDB/databaseAccounts"    = @{ label="Cosmos DB";           freeSku="Free"; note="One free Cosmos DB (1000 RU/s) per subscription" }
    "Microsoft.Sql/servers/databases"          = @{ label="Azure SQL";           freeSku="Free"; note="One free 32 GB serverless DB per subscription" }
    "Microsoft.Storage/storageAccounts"        = @{ label="Storage Account";     freeSku=$null;  note="Use LRS for lowest cost" }
    "Microsoft.CognitiveServices/accounts"     = @{ label="Azure AI/Cognitive";  freeSku="F0";   note="F0 sufficient for dev/hobby use" }
    "Microsoft.Search/searchServices"          = @{ label="Azure AI Search";     freeSku="free"; note="One free search service per subscription" }
    "microsoft.insights/components"            = @{ label="Application Insights"; freeSku=$null; note="Enable adaptive sampling to stay under 5 GB/month free" }
    "Microsoft.OperationalInsights/workspaces" = @{ label="Log Analytics";       freeSku="Free"; note="Free: 500 MB/day" }
    "Microsoft.KeyVault/vaults"                = @{ label="Key Vault";           freeSku=$null;  note="Consolidate vaults when possible" }
    "Microsoft.Network/publicIPAddresses"      = @{ label="Public IP";           freeSku=$null;  note="Delete IPs not attached to any resource" }
    "Microsoft.ServiceBus/namespaces"          = @{ label="Service Bus";         freeSku=$null;  note="Use Basic if only simple queues needed" }
    "Microsoft.SignalRService/SignalR"         = @{ label="SignalR";             freeSku="Free"; note="Free: 20 concurrent connections" }
}

# ── Main analysis ─────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Magenta
Write-Host "║   PoPunkouterSoftware — Azure Diagnostics Report    ║" -ForegroundColor Magenta
Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Magenta
Write-Host ""

# Set subscription if provided
if ($SubscriptionId) {
    Write-Step "Setting subscription $SubscriptionId"
    az account set --subscription $SubscriptionId | Out-Null
}

Write-Step "Getting account info..."
$account = Invoke-Az @("account", "show")
Write-Ok   "Subscription: $($account.name) ($($account.id))"
$subId = $account.id

$generatedAt = [datetime]::UtcNow

# ── Web services ─────────────────────────────────────────────────────────────

Write-Step "Discovering web apps..."
$webApps = Invoke-Az @("webapp", "list", "--query", "[?kind!='functionapp']", "--output", "json")
Write-Ok   "  Found $($webApps.Count) web apps"

Write-Step "Discovering Static Web Apps..."
$staticApps = Invoke-Az @("staticwebapp", "list", "--output", "json")
Write-Ok   "  Found $($staticApps.Count) static web apps"

Write-Step "Discovering Container Apps..."
try {
    $containerApps = Invoke-Az @("containerapp", "list", "--output", "json")
} catch {
    Write-Warn "Container Apps CLI extension not installed — skipping"
    $containerApps = @()
}
Write-Ok   "  Found $($containerApps.Count) container apps"

# ── HTTP connectivity + SSL tests ─────────────────────────────────────────────

Write-Step "Testing HTTP connectivity and SSL certificates..."
$services  = [System.Collections.Generic.List[psobject]]::new()
$sslExpiry = [System.Collections.Generic.List[psobject]]::new()

foreach ($app in $webApps) {
    $url  = if ($app.defaultHostName) { "https://$($app.defaultHostName)" } else { "" }
    $conn = if ($url) { Test-Url $url } else { @{ success = $false; error = "No URL" } }
    $status = if ($conn.success) { "active" } elseif ($conn.responseTime -gt 0) { "broken" } else { "unreachable" }

    $ssl = @{}
    if ($url) {
        $ssl = Get-SslExpiry -hostname $app.defaultHostName
        $sslExpiry.Add([pscustomobject]@{
            name     = $app.name
            url      = $url
            expiry   = $ssl.expiry
            daysLeft = $ssl.daysLeft
            subject  = $ssl.subject
            error    = $ssl.error
        })
    }

    $services.Add([pscustomobject]@{
        name          = $app.name
        friendlyName  = $app.name
        resourceGroup = $app.resourceGroup
        resourceType  = "Microsoft.Web/sites"
        url           = $url
        httpStatus    = $status
        platformState = $app.state
        connectivity  = [pscustomobject]@{
            success      = $conn.success
            responseTime = $conn.responseTime
            error        = $conn.error
        }
    })
}

foreach ($app in $staticApps) {
    $url  = if ($app.defaultHostname) { "https://$($app.defaultHostname)" } else { "" }
    $conn = if ($url) { Test-Url $url } else { @{ success = $false; error = "No URL" } }
    $status = if ($conn.success) { "active" } elseif ($conn.responseTime -gt 0) { "broken" } else { "unreachable" }

    if ($url) {
        $ssl = Get-SslExpiry -hostname $app.defaultHostname
        $sslExpiry.Add([pscustomobject]@{ name = $app.name; url = $url; expiry = $ssl.expiry; daysLeft = $ssl.daysLeft; subject = $ssl.subject; error = $ssl.error })
    }

    $services.Add([pscustomobject]@{
        name          = $app.name
        friendlyName  = $app.name
        resourceGroup = $app.resourceGroup
        resourceType  = "Microsoft.Web/staticSites"
        url           = $url
        httpStatus    = $status
        platformState = "Running"
        connectivity  = [pscustomobject]@{ success = $conn.success; responseTime = $conn.responseTime; error = $conn.error }
    })
}

foreach ($app in $containerApps) {
    $fqdn = $app.properties?.configuration?.ingress?.fqdn
    $url  = if ($fqdn) { "https://$fqdn" } else { "" }
    $conn = if ($url) { Test-Url $url } else { @{ success = $false; error = "No URL" } }
    $status = if ($conn.success) { "active" } elseif ($conn.responseTime -gt 0) { "broken" } else { "unreachable" }

    $services.Add([pscustomobject]@{
        name          = $app.name
        friendlyName  = $app.name
        resourceGroup = $app.resourceGroup
        resourceType  = "Microsoft.App/containerApps"
        url           = $url
        httpStatus    = $status
        platformState = "Running"
        connectivity  = [pscustomobject]@{ success = $conn.success; responseTime = $conn.responseTime; error = $conn.error }
    })
}

Write-Ok "Connectivity tests done ($($services.Count) services)"

# ── All resources + free tier ─────────────────────────────────────────────────

Write-Step "Listing all resources..."
$allResources = Invoke-Az @("resource", "list", "--output", "json")
Write-Ok   "  Found $($allResources.Count) resources"

$onFree    = [System.Collections.Generic.List[psobject]]::new()
$canGoFree = [System.Collections.Generic.List[psobject]]::new()
$noFree    = [System.Collections.Generic.List[psobject]]::new()

foreach ($r in $allResources) {
    $typeKey = $r.type
    if (-not $FreeTierMap.ContainsKey($typeKey)) { continue }
    $info       = $FreeTierMap[$typeKey]
    $currentSku = if ($r.sku?.name) { $r.sku.name } elseif ($r.kind) { $r.kind } else { "unknown" }
    $isOnFree   = $info.freeSku -and ($currentSku -ieq $info.freeSku)
    $canGo      = $info.freeSku -and -not $isOnFree

    $entry = [pscustomobject]@{
        name          = $r.name
        label         = $info.label
        currentSku    = $currentSku
        freeSku       = $info.freeSku
        resourceGroup = $r.resourceGroup
        recommendation = $info.note
    }
    if   ($isOnFree) { $onFree.Add($entry) }
    elseif ($canGo)  { $canGoFree.Add($entry) }
    else             { $noFree.Add($entry) }
}

$byType = $allResources | Group-Object -Property {
    ($_.type -split "/")[-1]
} | ForEach-Object { [pscustomobject]@{ type = $_.Name; count = $_.Count } }

# ── Cost data ─────────────────────────────────────────────────────────────────

Write-Step "Fetching 30-day cost data..."
try {
    $today    = [datetime]::UtcNow.Date
    $costFrom = $today.AddDays(-30).ToString("yyyy-MM-dd")
    $costTo   = $today.ToString("yyyy-MM-dd")
    $costBody = @{
        type       = "Usage"
        timeframe  = "Custom"
        timePeriod = @{ from = $costFrom; to = $costTo }
        dataset    = @{
            granularity = "None"
            aggregation = @{ totalCost = @{ name = "PreTaxCost"; function = "Sum" } }
            grouping    = @(
                @{ type = "Dimension"; name = "ServiceName" }
                @{ type = "Dimension"; name = "ResourceGroupName" }
            )
        }
    } | ConvertTo-Json -Depth 10

    $costResp = az rest --method POST `
        --url "https://management.azure.com/subscriptions/$subId/providers/Microsoft.CostManagement/query?api-version=2023-11-01" `
        --body $costBody `
        --headers "Content-Type=application/json" `
        --output json 2>&1 | ConvertFrom-Json

    $cols      = $costResp.properties.columns | ForEach-Object { $_.name.ToLower() }
    $costIdx   = [array]::IndexOf($cols, ($cols | Where-Object { $_ -match "pretax|cost" } | Select-Object -First 1))
    $svcIdx    = [array]::IndexOf($cols, ($cols | Where-Object { $_ -match "service" }   | Select-Object -First 1))
    $rgIdx     = [array]::IndexOf($cols, ($cols | Where-Object { $_ -match "resourcegroup" } | Select-Object -First 1))

    $costByKey  = @{}
    $totalCost  = 0.0
    foreach ($row in $costResp.properties.rows) {
        $cost  = if ($costIdx -ge 0) { [double]$row[$costIdx] } else { 0 }
        $svc   = if ($svcIdx  -ge 0) { "$($row[$svcIdx])" }   else { "Unknown" }
        $rg    = if ($rgIdx   -ge 0) { "$($row[$rgIdx])" }    else { "" }
        $key   = if ($rg) { "$svc ($rg)" } else { $svc }
        $costByKey[$key] = ($costByKey[$key] ?? 0) + $cost
        $totalCost += $cost
    }

    $topDrivers = $costByKey.GetEnumerator() |
        Where-Object { $_.Value -gt 0 } |
        Sort-Object Value -Descending |
        Select-Object -First 20 |
        ForEach-Object { [pscustomobject]@{ name = $_.Key; cost = [math]::Round($_.Value, 4) } }

    $costInfo = [pscustomobject]@{
        totalCost30Days = [math]::Round($totalCost, 4)
        totalFormatted  = "`$$([math]::Round($totalCost, 2).ToString('F2'))"
        topCostDrivers  = $topDrivers
        note            = if ($totalCost -eq 0) { "All costs `$0.00 — subscription may be covered by credits." } else { $null }
    }
    Write-Ok "Cost: $($costInfo.totalFormatted) over 30 days"
} catch {
    Write-Warn "Cost data unavailable: $_"
    $costInfo = [pscustomobject]@{ note = "Cost data unavailable: $_" }
}

# ── Config drift (App Services only) ─────────────────────────────────────────

Write-Step "Checking config drift on App Services..."
$configDrift = [System.Collections.Generic.List[psobject]]::new()
foreach ($app in $webApps) {
    try {
        $cfg    = Invoke-Az @("webapp", "config", "show", "--name", $app.name, "--resource-group", $app.resourceGroup, "--output", "json")
        $issues = [System.Collections.Generic.List[psobject]]::new()

        $ftpState = $cfg.ftpsState
        if ($ftpState -and $ftpState -ne "Disabled" -and $ftpState -ne "FtpsOnly") {
            $issues.Add([pscustomobject]@{ severity = "high"; issue = "FTP enabled ($ftpState) — use FTPS-only or Disabled" })
        }
        if ($cfg.http20Enabled -eq $false) {
            $issues.Add([pscustomobject]@{ severity = "low"; issue = "HTTP/2 disabled" })
        }
        $tls = $cfg.minTlsVersion
        if ($tls -and [version]$tls -lt [version]"1.2") {
            $issues.Add([pscustomobject]@{ severity = "high"; issue = "Min TLS $tls — must be ≥1.2" })
        }
        if ($cfg.alwaysOn -eq $false) {
            $issues.Add([pscustomobject]@{ severity = "low"; issue = "Always-On disabled (cold starts)" })
        }
        if ($cfg.cors?.allowedOrigins -contains "*") {
            $issues.Add([pscustomobject]@{ severity = "medium"; issue = "CORS * — all origins allowed" })
        }

        $configDrift.Add([pscustomobject]@{
            name          = $app.name
            resourceGroup = $app.resourceGroup
            issueCount    = $issues.Count
            issues        = $issues.ToArray()
        })
    } catch {
        Write-Warn "Config check failed for $($app.name): $_"
    }
}
Write-Ok "Config drift: checked $($webApps.Count) apps"

# ── Storage inventory ─────────────────────────────────────────────────────────

Write-Step "Inventorying storage accounts..."
$storageItems = [System.Collections.Generic.List[psobject]]::new()
$storageAccounts = $allResources | Where-Object { $_.type -ieq "Microsoft.Storage/storageAccounts" }
foreach ($sa in $storageAccounts) {
    try {
        $details    = Invoke-Az @("storage", "account", "show", "--name", $sa.name, "--resource-group", $sa.resourceGroup, "--output", "json")
        $publicBlob = $details.allowBlobPublicAccess -eq $true
        $httpsOnly  = $details.enableHttpsTrafficOnly -ne $false
        $minTls     = $details.minimumTlsVersion

        $issues = [System.Collections.Generic.List[psobject]]::new()
        if ($publicBlob) { $issues.Add([pscustomobject]@{ severity = "high";   issue = "Public blob access enabled" }) }
        if (-not $httpsOnly) { $issues.Add([pscustomobject]@{ severity = "high"; issue = "HTTPS-only is off" }) }
        if ($minTls -and $minTls -lt "TLS1_2") { $issues.Add([pscustomobject]@{ severity = "medium"; issue = "Min TLS $minTls — upgrade to TLS 1.2" }) }

        $storageItems.Add([pscustomobject]@{
            name             = $sa.name
            resourceGroup    = $sa.resourceGroup
            sku              = $details.sku?.name
            publicBlobAccess = $publicBlob
            httpsOnly        = $httpsOnly
            minTls           = $minTls
            issueCount       = $issues.Count
            issues           = $issues.ToArray()
        })
    } catch {
        Write-Warn "Storage check failed for $($sa.name): $_"
    }
}
Write-Ok "Storage: inventoried $($storageItems.Count) accounts"

# ── Build report object ────────────────────────────────────────────────────────

$activeCount    = ($services | Where-Object { $_.httpStatus -eq "active" }).Count
$brokenCount    = ($services | Where-Object { $_.httpStatus -eq "broken" }).Count
$otherCount     = $services.Count - $activeCount - $brokenCount

$report = [pscustomobject]@{
    generatedAt  = $generatedAt.ToString("o")
    subscription = [pscustomobject]@{ name = $account.name }
    webServices  = [pscustomobject]@{
        total    = $services.Count
        byStatus = [pscustomobject]@{ active = $activeCount; broken = $brokenCount; other = $otherCount }
        services = $services.ToArray()
    }
    cost               = $costInfo
    freeTier           = [pscustomobject]@{ onFree = $onFree.ToArray(); canGoFree = $canGoFree.ToArray(); noFreeTier = $noFree.ToArray() }
    allResourceSummary = [pscustomobject]@{ total = $allResources.Count; byType = $byType }
    sslExpiry          = $sslExpiry.ToArray()
    configDrift        = $configDrift.ToArray()
    storageInventory   = $storageItems.ToArray()
    appInsightsMetrics = @()
    zombieApps         = @()
    appsJsonDiff       = $null
}

# ── Write JSON ─────────────────────────────────────────────────────────────────

$dateStr  = $generatedAt.ToString("yyyy-MM-dd")
$jsonPath = Join-Path $OutputDir "azure-report-$dateStr.json"
$report | ConvertTo-Json -Depth 20 | Set-Content -Encoding UTF8 -Path $jsonPath
Write-Ok "JSON report → $jsonPath"

# ── Upload to Table Storage (optional) ────────────────────────────────────────

if ($UploadToTableStorage) {
    Write-Step "Uploading report to Azure Table Storage..."
    try {
        $jsonContent = Get-Content -Raw -Path $jsonPath
        $bytes       = [System.Text.Encoding]::UTF8.GetBytes($jsonContent)
        $chunkSize   = 60 * 1024   # 60 KB per chunk (Table Storage property limit is 64 KB)
        $totalChunks = [math]::Ceiling($bytes.Length / $chunkSize)

        $commonArgs = if ($TableStorageConnectionString) {
            @("--connection-string", $TableStorageConnectionString)
        } elseif ($StorageAccountName) {
            @("--account-name", $StorageAccountName, "--auth-mode", "login")
        } else {
            throw "Provide -StorageAccountName or -TableStorageConnectionString"
        }

        # Create table if it doesn't exist
        az storage table create --name "AzureReport" @commonArgs --output none 2>$null

        # Delete old chunks
        $existing = az storage entity query --table-name "AzureReport" `
            --filter "PartitionKey eq 'latest'" @commonArgs --output json 2>$null | ConvertFrom-Json
        foreach ($entity in $existing.items) {
            az storage entity delete --table-name "AzureReport" `
                --partition-key "latest" --row-key $entity.RowKey @commonArgs --output none 2>$null | Out-Null
        }

        # Upload chunks
        for ($i = 0; $i -lt $totalChunks; $i++) {
            $offset = $i * $chunkSize
            $length = [math]::Min($chunkSize, $bytes.Length - $offset)
            $chunk  = [System.Text.Encoding]::UTF8.GetString($bytes, $offset, $length)
            $rowKey = "chunk-{0:D3}" -f $i

            az storage entity insert --table-name "AzureReport" @commonArgs --output none --entity `
                "PartitionKey=latest" `
                "RowKey=$rowKey" `
                "Data=$chunk" `
                "GeneratedAt=$($generatedAt.ToString('o'))" `
                "TotalChunks=$totalChunks" | Out-Null
        }
        Write-Ok "Uploaded $totalChunks chunk(s) to Table Storage (table: AzureReport)"
    } catch {
        Write-Warn "Table Storage upload failed: $_"
    }
}

# ── Generate HTML report ───────────────────────────────────────────────────────

Write-Step "Generating HTML report..."

function Get-StatusBadge([string]$status) {
    switch ($status) {
        "active"     { return '<span style="background:#22c55e;color:#fff;padding:2px 8px;border-radius:4px;font-size:12px">active</span>' }
        "broken"     { return '<span style="background:#ef4444;color:#fff;padding:2px 8px;border-radius:4px;font-size:12px">broken</span>' }
        "unreachable"{ return '<span style="background:#f97316;color:#fff;padding:2px 8px;border-radius:4px;font-size:12px">unreachable</span>' }
        default      { return '<span style="background:#6b7280;color:#fff;padding:2px 8px;border-radius:4px;font-size:12px">unknown</span>' }
    }
}

function Get-SeverityColor([string]$sev) {
    switch ($sev) {
        "high"   { return "#ef4444" }
        "medium" { return "#f97316" }
        default  { return "#eab308" }
    }
}

$serviceRows = ($services | ForEach-Object {
    $badge = Get-StatusBadge $_.httpStatus
    $url   = if ($_.url) { "<a href='$($_.url)' target='_blank'>$($_.url)</a>" } else { "—" }
    "<tr><td>$($_.name)</td><td>$url</td><td>$badge</td><td>$($_.connectivity.responseTime) ms</td><td>$($_.resourceGroup)</td></tr>"
}) -join "`n"

$sslRows = ($sslExpiry | ForEach-Object {
    $color = if ($_.daysLeft -lt 30) { "#ef4444" } elseif ($_.daysLeft -lt 60) { "#f97316" } else { "#22c55e" }
    $days  = if ($null -ne $_.daysLeft) { "<span style='color:$color;font-weight:bold'>$($_.daysLeft)d</span>" } else { "<span style='color:#6b7280'>N/A</span>" }
    "<tr><td>$($_.name)</td><td>$($_.expiry ?? '—')</td><td>$days</td><td>$($_.error ?? '')</td></tr>"
}) -join "`n"

$configRows = ($configDrift | Where-Object { $_.issueCount -gt 0 } | ForEach-Object {
    $issueHtml = ($_.issues | ForEach-Object { "<li style='color:$(Get-SeverityColor $_.severity)'>[$($_.severity.ToUpper())] $($_.issue)</li>" }) -join ""
    "<tr><td>$($_.name)</td><td>$($_.resourceGroup)</td><td>$($_.issueCount)</td><td><ul style='margin:0;padding-left:18px'>$issueHtml</ul></td></tr>"
}) -join "`n"

$freeTierRows = ($canGoFree | ForEach-Object {
    "<tr><td>$($_.name)</td><td>$($_.label)</td><td>$($_.currentSku)</td><td>$($_.freeSku)</td><td>$($_.resourceGroup)</td><td>$($_.recommendation)</td></tr>"
}) -join "`n"

$costRows = if ($report.cost.topCostDrivers) {
    ($report.cost.topCostDrivers | Select-Object -First 15 | ForEach-Object {
        "<tr><td>$($_.name)</td><td style='text-align:right'>`$$($_.cost)</td></tr>"
    }) -join "`n"
} else { "" }

$html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Azure Diagnostics Report — $($account.name) — $dateStr</title>
<style>
  :root{--bg:#0f172a;--surface:#1e293b;--border:#334155;--text:#e2e8f0;--muted:#94a3b8;--accent:#818cf8}
  *{box-sizing:border-box;margin:0;padding:0}
  body{font-family:'Segoe UI',system-ui,sans-serif;background:var(--bg);color:var(--text);padding:24px}
  h1{font-size:2rem;color:var(--accent);margin-bottom:4px}
  h2{font-size:1.25rem;color:var(--accent);margin:32px 0 12px;border-bottom:1px solid var(--border);padding-bottom:6px}
  .meta{color:var(--muted);margin-bottom:32px;font-size:.9rem}
  .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:16px;margin-bottom:32px}
  .card{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:16px;text-align:center}
  .card .num{font-size:2.5rem;font-weight:700;color:var(--accent)}
  .card .lbl{color:var(--muted);font-size:.85rem;margin-top:4px}
  table{width:100%;border-collapse:collapse;background:var(--surface);border-radius:8px;overflow:hidden;margin-bottom:16px}
  th{background:#283548;padding:10px 14px;text-align:left;font-size:.8rem;text-transform:uppercase;color:var(--muted)}
  td{padding:9px 14px;border-top:1px solid var(--border);font-size:.875rem;vertical-align:top}
  a{color:var(--accent)}
  .note{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:14px;color:var(--muted);font-size:.875rem}
</style>
</head>
<body>
<h1>⚡ Azure Diagnostics Report</h1>
<p class="meta">Subscription: <strong>$($account.name)</strong> &nbsp;|&nbsp; Generated: <strong>$($generatedAt.ToString('yyyy-MM-dd HH:mm')} UTC</strong></p>

<div class="cards">
  <div class="card"><div class="num">$($services.Count)</div><div class="lbl">Total Services</div></div>
  <div class="card"><div class="num" style="color:#22c55e">$activeCount</div><div class="lbl">Active</div></div>
  <div class="card"><div class="num" style="color:#ef4444">$brokenCount</div><div class="lbl">Broken</div></div>
  <div class="card"><div class="num">$($allResources.Count)</div><div class="lbl">Total Resources</div></div>
  <div class="card"><div class="num">$($costInfo.totalFormatted ?? '—')</div><div class="lbl">30-Day Cost</div></div>
  <div class="card"><div class="num" style="color:#f97316">$($canGoFree.Count)</div><div class="lbl">Can Go Free</div></div>
</div>

<h2>🌐 Web Services</h2>
<table>
  <tr><th>Name</th><th>URL</th><th>Status</th><th>Response</th><th>Resource Group</th></tr>
  $serviceRows
</table>

<h2>🔒 SSL Certificates</h2>
<table>
  <tr><th>Service</th><th>Expires</th><th>Days Left</th><th>Error</th></tr>
  $sslRows
</table>

<h2>💰 Cost (30 Days)</h2>
$(if ($costRows) {
    "<table><tr><th>Service</th><th style='text-align:right'>Cost</th></tr>$costRows</table>"
} else {
    "<p class='note'>$($costInfo.note ?? 'No cost data available.')</p>"
})

<h2>⚙️ Config Drift</h2>
$(if ($configRows) {
    "<table><tr><th>App</th><th>Resource Group</th><th>Issues</th><th>Details</th></tr>$configRows</table>"
} else {
    "<p class='note'>✅ No config issues found on $(($webApps).Count) App Services checked.</p>"
})

<h2>🆓 Free-Tier Opportunities</h2>
$(if ($freeTierRows) {
    "<table><tr><th>Resource</th><th>Type</th><th>Current SKU</th><th>Free SKU</th><th>Resource Group</th><th>Tip</th></tr>$freeTierRows</table>"
} else {
    "<p class='note'>✅ No free-tier savings found — resources already optimised.</p>"
})

<h2>📦 Storage Accounts</h2>
$(if ($storageItems.Count -gt 0) {
    $storRows = ($storageItems | ForEach-Object {
        $issHtml = ($_.issues | ForEach-Object { "<li style='color:$(Get-SeverityColor $_.severity)'>$($_.issue)</li>" }) -join ""
        "<tr><td>$($_.name)</td><td>$($_.resourceGroup)</td><td>$($_.sku)</td><td>$($_.issueCount)</td><td><ul style='margin:0;padding-left:18px'>$issHtml</ul></td></tr>"
    }) -join "`n"
    "<table><tr><th>Name</th><th>RG</th><th>SKU</th><th>Issues</th><th>Details</th></tr>$storRows</table>"
} else {
    "<p class='note'>No storage accounts found.</p>"
})

</body>
</html>
"@

$htmlPath = Join-Path $OutputDir "azure-report-$dateStr.html"
$html | Set-Content -Encoding UTF8 -Path $htmlPath

Write-Host ""
Write-Ok "HTML report → $htmlPath"
Write-Host ""
Write-Host "Opening report…" -ForegroundColor Cyan
Start-Process $htmlPath
