#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK\Agent",
    [string]$DataPath = "$env:ProgramData\SIRK\Agent",
    [string]$ServiceName = 'SirkAgent',
    [string]$WatchdogServiceName = 'SirkAgentWatchdog',
    [ValidateSet('dev','stable')]
    [string]$Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$updaterRoot = Join-Path $env:ProgramFiles 'SIRK\Updater'
$updaterCli = Join-Path $updaterRoot 'SirkUpdater.exe'

if (-not (Test-Path -LiteralPath $updaterCli)) {
    Write-Host '=== Install shared SIRK Updater ==='
    $bootstrap = Join-Path $env:TEMP ('sirk-updater-install-' + [guid]::NewGuid().ToString('N') + '.ps1')
    try {
        Invoke-WebRequest `
            -Uri ('https://raw.githubusercontent.com/Eris92/SIRK-Updater/main/install-release.ps1?nocache=' + [guid]::NewGuid()) `
            -OutFile $bootstrap `
            -UseBasicParsing
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bootstrap -AllowSourceFallback
        if ($LASTEXITCODE -ne 0) {
            throw "SIRK Updater installer failed with ExitCode=$LASTEXITCODE."
        }
    }
    finally {
        Remove-Item -LiteralPath $bootstrap -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $updaterCli)) {
    throw "SIRK Updater CLI is missing after installation: $updaterCli"
}

$agentService = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
if (-not $agentService) {
    throw "SIRK Agent service is not installed: $ServiceName"
}

$watchdogService = Get-CimInstance Win32_Service -Filter "Name='$WatchdogServiceName'" -ErrorAction SilentlyContinue

$manifestPath = Join-Path $env:TEMP ('sirk-agent-updater-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    $manifest = [ordered]@{
        schemaVersion       = 1
        applicationId       = 'sirk-agent'
        displayName         = 'SIRK Agent'
        serviceName         = $agentService.Name
        watchdogServiceName = if ($watchdogService) { $watchdogService.Name } else { $null }
        installRoot         = $InstallPath
        dataRoot            = $DataPath
        healthUrl           = $null
        channel             = $Channel
        updateSource        = 'https://github.com/Eris92/SIRK-Agent'
        signatureRequired   = $true
    }

    $manifest |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $manifestPath -Encoding UTF8

    & $updaterCli register $manifestPath
    if ($LASTEXITCODE -ne 0) {
        throw "SIRK Updater registration failed with ExitCode=$LASTEXITCODE."
    }

    & $updaterCli show sirk-agent
    if ($LASTEXITCODE -ne 0) {
        throw 'SIRK Updater could not read the registered Agent manifest.'
    }
}
finally {
    Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
}

Write-Host 'SIRK_AGENT_UPDATER_REGISTERED' -ForegroundColor Green
