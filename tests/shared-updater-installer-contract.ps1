#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$register = Join-Path $root 'tools\package\Register-SirkUpdater.ps1'
$wrapper = Join-Path $root 'tools\package\Install-SirkAgent-WithUpdater.ps1'
foreach ($file in @($register, $wrapper)) {
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing file: $file" }
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw ($errors | ForEach-Object Message | Out-String) }
}

$registerText = Get-Content -LiteralPath $register -Raw
$wrapperText = Get-Content -LiteralPath $wrapper -Raw
$requiredRegister = @(
    'install-release.ps1',
    '-AllowSourceFallback',
    "applicationId       = 'sirk-agent'",
    "serviceName         = $agentService.Name",
    "signatureRequired   = $true",
    'SIRK_AGENT_UPDATER_REGISTERED'
)
foreach ($needle in $requiredRegister) {
    if ($registerText.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Agent Updater registration contract is missing: $needle"
    }
}
foreach ($needle in @('Install-SirkAgent.ps1', 'Register-SirkUpdater.ps1', 'SIRK_AGENT_INSTALLATION_WITH_UPDATER_OK')) {
    if ($wrapperText.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Agent installer wrapper contract is missing: $needle"
    }
}

Write-Host 'shared-updater-installer-contract: OK'
