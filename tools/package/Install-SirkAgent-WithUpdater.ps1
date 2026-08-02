#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK\Agent",
    [string]$ServiceName = 'SirkAgent',
    [string]$WatchdogServiceName = 'SirkAgentWatchdog',
    [ValidateSet('dev','stable')]
    [string]$Channel = 'stable',
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$coreInstaller = Join-Path $root 'Install-SirkAgent.ps1'
$registerUpdater = Join-Path $root 'Register-SirkUpdater.ps1'

if (-not (Test-Path -LiteralPath $coreInstaller)) {
    throw "Core Agent installer is missing: $coreInstaller"
}
if (-not (Test-Path -LiteralPath $registerUpdater)) {
    throw "SIRK Updater registration script is missing: $registerUpdater"
}

& $coreInstaller `
    -InstallPath $InstallPath `
    -ServiceName $ServiceName `
    -WatchdogServiceName $WatchdogServiceName `
    -NoStart:$NoStart

& $registerUpdater `
    -InstallPath $InstallPath `
    -ServiceName $ServiceName `
    -WatchdogServiceName $WatchdogServiceName `
    -Channel $Channel

Write-Host 'SIRK_AGENT_INSTALLATION_WITH_UPDATER_OK' -ForegroundColor Green
