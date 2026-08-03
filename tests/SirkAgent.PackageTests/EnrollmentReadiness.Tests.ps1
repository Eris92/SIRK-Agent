$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script = Get-Content (Join-Path $PSScriptRoot '..\..\tools\package\Install-SirkAgent-WithUpdater.ps1') -Raw -Encoding UTF8
foreach ($marker in @(
    "heartbeat-latest.json",
    "Waiting for SIRK Agent identity and heartbeat",
    "tenantId",
    "deviceId",
    "AddSeconds(90)",
    "Get-WinEvent",
    "SIRK Agent did not become ready for enrollment"
)) {
    if (-not $script.Contains($marker)) {
        throw "Enrollment readiness contract is missing: $marker"
    }
}

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count) {
    $errors | ForEach-Object { Write-Error $_.Message }
    throw 'Install-SirkAgent-WithUpdater.ps1 has invalid PowerShell syntax.'
}

Write-Host 'SIRK Agent enrollment readiness package contract: OK' -ForegroundColor Green
