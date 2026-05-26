param(
    [switch]$SkipWinget,
    [switch]$SkipDocker,
    [switch]$SkipAzLogin
)

$ErrorActionPreference = 'Stop'

Write-Host '[setup] Starting PoPunkouterSoftware first-run setup...'

# Ensure stale local API processes do not lock development ports.
$dotnetProcesses = Get-Process dotnet -ErrorAction SilentlyContinue
if ($dotnetProcesses) {
    Write-Host '[setup] Stopping existing dotnet processes to free ports 5000/5001...'
    $dotnetProcesses | Stop-Process -Force
}

if (-not $SkipWinget) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host '[setup] Verifying required tools with winget...'
        winget --version | Out-Null
    }
    else {
        Write-Warning '[setup] winget not found. Install App Installer from Microsoft Store or rerun with -SkipWinget.'
    }
}

if (-not $SkipDocker) {
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host '[setup] Ensuring Docker daemon is available...'
        try {
            docker info | Out-Null
        }
        catch {
            Write-Warning '[setup] Docker is installed but daemon is not ready. Start Docker Desktop and rerun setup.'
        }
    }
    else {
        Write-Warning '[setup] docker command not found. Install Docker Desktop or rerun with -SkipDocker.'
    }
}

if (-not $SkipAzLogin) {
    if (Get-Command az -ErrorAction SilentlyContinue) {
        Write-Host '[setup] Checking Azure CLI authentication for Key Vault access...'
        $account = az account show --query id -o tsv 2>$null
        if (-not $account) {
            Write-Host '[setup] Azure CLI is not authenticated. Running az login...'
            az login | Out-Null
        }
        else {
            Write-Host "[setup] Azure CLI authenticated (subscription: $account)."
        }
    }
    else {
        Write-Warning '[setup] az command not found. Install Azure CLI or rerun with -SkipAzLogin.'
    }
}

Write-Host '[setup] Setup checks complete.'
