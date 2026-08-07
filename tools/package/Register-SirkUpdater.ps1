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
    Write-Host '=== Install verified shared SIRK Updater release ==='
    $bootstrap = Join-Path $env:TEMP ('sirk-updater-install-' + [guid]::NewGuid().ToString('N') + '.ps1')
    try {
        Invoke-WebRequest `
            -Uri ('https://raw.githubusercontent.com/Eris92/SIRK-Updater/main/install-release-v2.ps1?nocache=' + [guid]::NewGuid()) `
            -OutFile $bootstrap `
            -UseBasicParsing
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bootstrap | Out-Host
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) { throw "SIRK Updater release installer failed with ExitCode=$exitCode." }
    }
    finally { Remove-Item -LiteralPath $bootstrap -Force -ErrorAction SilentlyContinue }
}
if (-not (Test-Path -LiteralPath $updaterCli)) { throw "SIRK Updater CLI is missing after installation: $updaterCli" }

$agentService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $agentService) { throw "SIRK Agent service is not installed: $ServiceName" }
$watchdogService = Get-Service -Name $WatchdogServiceName -ErrorAction SilentlyContinue
$signatureVerifier = Join-Path $InstallPath 'sirkctl.exe'
if (-not (Test-Path -LiteralPath $signatureVerifier -PathType Leaf)) { throw "SIRK Agent signature verifier is missing: $signatureVerifier" }
$releaseTrustKeyring = Join-Path $InstallPath 'release-trusted-keys.json'
if (-not (Test-Path -LiteralPath $releaseTrustKeyring -PathType Leaf)) { throw "SIRK Agent release trust keyring is missing: $releaseTrustKeyring" }

$manifestPath = Join-Path $env:TEMP ('sirk-agent-updater-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [ordered]@{
        schemaVersion               = 1
        applicationId               = 'sirk-agent'
        displayName                 = 'SIRK Agent'
        serviceName                 = $agentService.Name
        watchdogServiceName         = if ($watchdogService) { $watchdogService.Name } else { $null }
        installRoot                 = $InstallPath
        dataRoot                    = $DataPath
        healthUrl                   = $null
        channel                     = $Channel
        updateSource                = 'sirk-central-cache'
        signatureRequired           = $true
        signatureVerifierPath       = $signatureVerifier
        signatureVerifierArguments = @('verify-update', '--package', '{payload}', '--trusted-keys', $releaseTrustKeyring)
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    & $updaterCli register $manifestPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SIRK Updater registration failed with ExitCode=$LASTEXITCODE." }
    & $updaterCli show sirk-agent | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'SIRK Updater could not read the registered Agent manifest.' }
}
finally { Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue }

Write-Host 'SIRK_AGENT_UPDATER_REGISTERED' -ForegroundColor Green
