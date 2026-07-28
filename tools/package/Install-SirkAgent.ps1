#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK Agent",
    [string]$ServiceName = "SirkAgent",
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$exeName = 'SirkAgent.Service.exe'
$sourceExe = Join-Path $source $exeName

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Brak pliku $exeName w katalogu pakietu: $source"
}

$runtime = & dotnet --list-runtimes 2>$null
if (-not ($runtime -match '^Microsoft\.NETCore\.App 8\.')) {
    throw 'Brak Microsoft .NET 8 Runtime x64. Zainstaluj: winget install Microsoft.DotNet.Runtime.8'
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
Get-ChildItem -LiteralPath $source -File | Where-Object {
    $_.Name -notlike '*.zip' -and $_.Name -notlike 'TestBundle-*'
} | Copy-Item -Destination $InstallPath -Force

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

if (-not $NoStart) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}

Write-Host "SIRK Agent zainstalowany: $InstallPath" -ForegroundColor Green
Get-Service -Name $ServiceName | Format-Table Name, Status, StartType -AutoSize
