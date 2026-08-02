#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK\Agent",
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

$existingWatchdog = Get-Service -Name $WatchdogServiceName -ErrorAction SilentlyContinue
if ($existingWatchdog) {
    if ($existingWatchdog.Status -ne 'Stopped') {
        Stop-Service -Name $WatchdogServiceName -Force
        $existingWatchdog.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $WatchdogServiceName | Out-Null
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
$sessionProcesses = @(Get-Process 'SirkAgent.Session' -ErrorAction SilentlyContinue)
$sessionProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
$sessionProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
$packageFiles = Get-ChildItem -LiteralPath $source -File | Where-Object {
    $_.Name -notlike '*.zip' -and $_.Name -notlike 'TestBundle-*'
}
foreach ($packageFile in $packageFiles) {
    $destination = Join-Path $InstallPath $packageFile.Name
    $copied = $false
    for ($attempt = 1; $attempt -le 10 -and -not $copied; $attempt++) {
        try {
            Copy-Item -LiteralPath $packageFile.FullName -Destination $destination -Force
            $copied = $true
        } catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

$sessionSource = Join-Path $source 'Session'
$sessionTarget = Join-Path $InstallPath 'Session'
if (Test-Path -LiteralPath $sessionSource) {
    New-Item -ItemType Directory -Path $sessionTarget -Force | Out-Null
    foreach ($sessionFile in Get-ChildItem -LiteralPath $sessionSource -File -Recurse) {
        $relative = $sessionFile.FullName.Substring($sessionSource.TrimEnd('\').Length).TrimStart('\')
        $destination = Join-Path $sessionTarget $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        for ($attempt = 1; $attempt -le 10; $attempt++) {
            try { Copy-Item -LiteralPath $sessionFile.FullName -Destination $destination -Force; break }
            catch { if ($attempt -eq 10) { throw }; Start-Sleep -Milliseconds 500 }
        }
    }
}

$extensionSource = Join-Path $source 'BrowserExtension'
$extensionTarget = Join-Path $InstallPath 'BrowserExtension'
if (Test-Path -LiteralPath $extensionSource) {
    if (Test-Path -LiteralPath $extensionTarget) {
        Remove-Item -LiteralPath $extensionTarget -Recurse -Force
    }
    Copy-Item -LiteralPath $extensionSource -Destination $extensionTarget -Recurse -Force
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
        try {
            Start-Process -FilePath $sessionExe -WindowStyle Hidden
        } catch {
            Write-Warning "Broker sesji zostanie uruchomiony przy następnym logowaniu: $($_.Exception.Message)"
        }
    }
}

$dataPath = Join-Path $env:ProgramData 'SIRK\Agent'
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
& icacls.exe $dataPath /inheritance:r `
    /grant:r '*S-1-5-18:(OI)(CI)F' `
    '*S-1-5-32-544:(OI)(CI)F' `
    '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Nie udalo sie zabezpieczyc ACL katalogu danych: $dataPath"
}

$targetExe = Join-Path $InstallPath $exeName
& sc.exe create $ServiceName binPath= ('"{0}"' -f $targetExe) start= auto DisplayName= 'SIRK Agent' | Out-Null
& sc.exe description $ServiceName 'SIRK Agent security runtime and diagnostics service.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

$watchdogExe = Join-Path $InstallPath $watchdogExeName
if (-not (Test-Path -LiteralPath $watchdogExe)) {
    throw "Brak pliku $watchdogExeName w pakiecie."
}
& sc.exe create $WatchdogServiceName binPath= ('"{0}"' -f $watchdogExe) `
    start= delayed-auto DisplayName= 'SIRK Agent Watchdog' | Out-Null
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

Write-Host "SIRK Agent zainstalowany: $InstallPath" -ForegroundColor Green
Get-Service -Name $ServiceName, $WatchdogServiceName | Format-Table Name, Status, StartType -AutoSize
