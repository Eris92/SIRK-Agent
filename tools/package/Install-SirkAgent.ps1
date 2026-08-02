#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK\Agent",
    [string]$DataPath = "$env:ProgramData\SIRK\Agent",
    [string]$ServiceName = "SirkAgent",
    [string]$WatchdogServiceName = "SirkAgentWatchdog",
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$exeName = 'SirkAgent.Service.exe'
$sourceExe = Join-Path $source $exeName
$watchdogExeName = 'SirkAgent.Watchdog.exe'

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Brak pliku $exeName w katalogu pakietu: $source"
}

$runtime = & dotnet --list-runtimes 2>$null
if (-not ($runtime -match '^Microsoft\.NETCore\.App 10\.')) {
    throw 'Brak Microsoft .NET 10 Runtime x64. Zainstaluj: winget install Microsoft.DotNet.Runtime.10'
}

foreach ($name in @($WatchdogServiceName, $ServiceName)) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $name -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete $name | Out-Null
    }
}
Start-Sleep -Seconds 2

$sessionProcesses = @(Get-Process 'SirkAgent.Session' -ErrorAction SilentlyContinue)
$sessionProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
$sessionProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $InstallPath) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}
if (Test-Path -LiteralPath $DataPath) {
    Remove-Item -LiteralPath $DataPath -Recurse -Force
}
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'SIRKAgentSession' -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
$packageFiles = Get-ChildItem -LiteralPath $source -File | Where-Object {
    $_.Name -notlike '*.zip' -and $_.Name -notlike 'TestBundle-*'
}
foreach ($packageFile in $packageFiles) {
    Copy-Item -LiteralPath $packageFile.FullName -Destination (Join-Path $InstallPath $packageFile.Name) -Force
}

foreach ($directoryName in @('Session', 'BrowserExtension')) {
    $directorySource = Join-Path $source $directoryName
    if (Test-Path -LiteralPath $directorySource) {
        Copy-Item -LiteralPath $directorySource -Destination (Join-Path $InstallPath $directoryName) -Recurse -Force
    }
}

$browserInstaller = Join-Path $InstallPath 'Install-SirkBrowserBridge.ps1'
if ((Test-Path -LiteralPath (Join-Path $InstallPath 'SirkAgent.BrowserHost.exe')) -and
    (Test-Path -LiteralPath $browserInstaller)) {
    & $browserInstaller -InstallPath $InstallPath
}

$sessionExe = Join-Path $InstallPath 'Session\SirkAgent.Session.exe'
if (-not (Test-Path -LiteralPath $sessionExe)) {
    $sessionExe = Join-Path $InstallPath 'SirkAgent.Session.exe'
}
if (Test-Path -LiteralPath $sessionExe) {
    $runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name 'SIRKAgentSession' -Value ('"{0}"' -f $sessionExe) `
        -PropertyType String -Force | Out-Null
    if ([System.Diagnostics.Process]::GetCurrentProcess().SessionId -gt 0) {
        Start-Process -FilePath $sessionExe -WindowStyle Hidden
    }
}

New-Item -ItemType Directory -Path $DataPath -Force | Out-Null
& icacls.exe $DataPath /inheritance:r `
    /grant:r '*S-1-5-18:(OI)(CI)F' `
    '*S-1-5-32-544:(OI)(CI)F' `
    '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Nie udalo sie zabezpieczyc ACL katalogu danych: $DataPath"
}

$targetExe = Join-Path $InstallPath $exeName
& sc.exe create $ServiceName binPath= ('"{0}"' -f $targetExe) start= auto DisplayName= 'SIRK Agent' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Nie udalo sie utworzyc uslugi $ServiceName." }
& sc.exe description $ServiceName 'SIRK Agent security runtime and diagnostics service.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

$watchdogExe = Join-Path $InstallPath $watchdogExeName
if (-not (Test-Path -LiteralPath $watchdogExe)) {
    throw "Brak pliku $watchdogExeName w pakiecie."
}
& sc.exe create $WatchdogServiceName binPath= ('"{0}"' -f $watchdogExe) `
    start= delayed-auto DisplayName= 'SIRK Agent Watchdog' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Nie udalo sie utworzyc uslugi $WatchdogServiceName." }
& sc.exe description $WatchdogServiceName `
    'Minimal watchdog, recovery and signed update coordinator for SIRK Agent.' | Out-Null
& sc.exe failure $WatchdogServiceName reset= 86400 `
    actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $WatchdogServiceName 1 | Out-Null

if (-not $NoStart) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Start-Service -Name $WatchdogServiceName
    (Get-Service -Name $WatchdogServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}

Write-Host "SIRK Agent clean install completed: $InstallPath" -ForegroundColor Green
Get-Service -Name $ServiceName, $WatchdogServiceName | Format-Table Name, Status, StartType -AutoSize
Write-Host 'SIRK_AGENT_CLEAN_INSTALL_OK' -ForegroundColor Green
