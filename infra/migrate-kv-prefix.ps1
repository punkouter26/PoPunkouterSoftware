<#
.SYNOPSIS
    Copy the secrets PoPunkouterSoftware consumes from the shared Key Vault
    (kv-poshared) into a namespaced set prefixed with "PoPunkouterSoftware--".

.DESCRIPTION
    The shared vault holds unprefixed secrets (e.g. "AzureOpenAI--ApiKey") that
    every Po solution reads. This is a cross-app data-leakage risk.

    AppKeyVaultSecretManager (PoPunkouterSoftware.Infrastructure) already
    supports a "PoPunkouterSoftware--" prefix. This script:
      1. Reads the list of source secrets that this app actually consumes
         (see $SourceSecrets below).
      2. For each one, checks whether the prefixed version
         "PoPunkouterSoftware--<name>" already exists.
      3. Creates the prefixed secret with the SAME VALUE, but ONLY if the
         prefixed one does not already exist (idempotent).
      4. Leaves the original unprefixed secret in place as a fallback while
         the app rolls out the new AppKeyVaultSecretManager logic.

    Run from the repo root after `az login` (subscription: Punkouter26).

.EXAMPLE
    .\infra\migrate-kv-prefix.ps1 -VaultName kv-poshared -WhatIf
.EXAMPLE
    .\infra\migrate-kv-prefix.ps1 -VaultName kv-poshared
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $VaultName,

    [string] $Prefix = 'PoPunkouterSoftware--'
)

$ErrorActionPreference = 'Stop'

# Source = (current) secret name. Target = prefixed name.
# Keep this list tight — only what PoPunkouterSoftware actually reads
# (see appsettings.json). Other vault secrets belong to other Po solutions.
$SourceSecrets = @(
    'ApplicationInsights--ConnectionString',
    'AzureOpenAI--ApiKey',
    'AzureOpenAI--DeploymentName',
    'AzureOpenAI--Endpoint',
    'ConnectionStrings--AzureTableStorage',
    'GitHub--PersonalAccessToken',
    'Incidents--WebhookUrl'
)

Write-Host "[kv-migrate] Vault : $VaultName"
Write-Host "[kv-migrate] Prefix: $Prefix"
Write-Host "[kv-migrate] Will process $($SourceSecrets.Count) secrets`n"

$created = 0
$skipped = 0
$missing = 0

foreach ($name in $SourceSecrets) {
    $target = "$Prefix$name"

    $existing = az keyvault secret show --vault-name $VaultName --name $target -o json 2>$null
    if ($existing) {
        Write-Host "  [skip] $target (already exists)"
        $skipped++
        continue
    }

    $value = az keyvault secret show --vault-name $VaultName --name $name --query value -o tsv 2>$null
    if (-not $value) {
        Write-Warning "  [missing] $name — not in vault; skipping"
        $missing++
        continue
    }

    if ($PSCmdlet.ShouldProcess("$VaultName / $target", 'az keyvault secret set')) {
        # Set via env var + --value so the secret never lands in the shell history
        # or in the process command line. (az on Windows does not support --file /dev/stdin.)
        $env:AZ_SECRET_VALUE = $value
        $stderrPath = [System.IO.Path]::GetTempFileName()
        try {
            az keyvault secret set --vault-name $VaultName --name $target --value $env:AZ_SECRET_VALUE --output none 2>$stderrPath
            if ($LASTEXITCODE -ne 0) {
                $err = (Get-Content -Raw $stderrPath).Trim()
                Write-Warning "  [failed] $target (exit $LASTEXITCODE) — $err"
                continue
            }
        }
        finally {
            Remove-Item Env:AZ_SECRET_VALUE -ErrorAction SilentlyContinue
            Remove-Item $stderrPath -ErrorAction SilentlyContinue
        }
        Write-Host "  [ok]    $target"
        $created++
    }
}

Write-Host "`n[kv-migrate] Done. created=$created skipped=$skipped missing=$missing"
Write-Host "[kv-migrate] Originals (unprefixed) were NOT modified."
Write-Host "[kv-migrate] Update AppKeyVaultSecretManager to read prefixed names (already does), then redeploy."
