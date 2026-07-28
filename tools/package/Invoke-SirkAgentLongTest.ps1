#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet(24,48)]
    [int]$Hours = 24,
    [int]$CheckMinutes = 5,
    [switch]$TestScmRecovery
)

$ErrorActionPreference = 'Stop'
$Root = Join-Path $env:ProgramData 'SIRK\Agent'
$Log = Join-Path $Root ("long-test-{0:yyyyMMdd-HHmmss}.jsonl" -f (Get-Date))
$Started = Get-Date
$Deadline = $Started.AddHours($Hours)

function Write-TestRecord {
    param([hashtable]$Data)
    $Data.timestampUtc = [DateTimeOffset]::UtcNow
    ($Data | ConvertTo-Json -Compress -Depth 8) | Add-Content -LiteralPath $Log -Encoding UTF8
}

$Service = Get-Service SirkAgent -ErrorAction Stop
if ($Service.Status -ne 'Running') { Start-Service SirkAgent }
$DeviceBefore = (& "$PSScriptRoot\sirkctl.exe" status | ConvertFrom-Json).heartbeat.deviceId

if ($TestScmRecovery) {
    $PidBefore = (Get-CimInstance Win32_Service -Filter "Name='SirkAgent'").ProcessId
    Stop-Process -Id $PidBefore -Force
    $Recovered = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep 1
        $Svc = Get-CimInstance Win32_Service -Filter "Name='SirkAgent'"
        if ($Svc.State -eq 'Running' -and $Svc.ProcessId -gt 0 -and $Svc.ProcessId -ne $PidBefore) {
            $Recovered = $true
            Write-TestRecord @{ event='ScmRecovery'; result='Success'; oldPid=$PidBefore; newPid=$Svc.ProcessId }
            break
        }
    }
    if (-not $Recovered) { throw 'SCM nie odzyskal uslugi po zabiciu procesu.' }
}

Write-Host "Test SIRK Agent uruchomiony na $Hours godzin. Log: $Log" -ForegroundColor Cyan
while ((Get-Date) -lt $Deadline) {
    $Svc = Get-CimInstance Win32_Service -Filter "Name='SirkAgent'"
    $Status = & "$PSScriptRoot\sirkctl.exe" status | ConvertFrom-Json
    $RuntimePath = Join-Path $Root 'runtime-health.json'
    $EndurancePath = Join-Path $Root 'endurance-summary.json'
    $Runtime = if (Test-Path $RuntimePath) { Get-Content $RuntimePath -Raw | ConvertFrom-Json } else { $null }
    $Endurance = if (Test-Path $EndurancePath) { Get-Content $EndurancePath -Raw | ConvertFrom-Json } else { $null }

    Write-TestRecord @{
        event='Sample'
        serviceState=$Svc.State
        processId=$Svc.ProcessId
        deviceId=$Status.heartbeat.deviceId
        securityState=$Status.security.security.state
        overallHealth=$Status.security.overallHealth
        heartbeatFresh=$Runtime.heartbeatFresh
        cpuPercent=$Runtime.cpuPercent
        workingSetBytes=$Runtime.workingSetBytes
        enduranceStatus=$Endurance.status
        samples=$Endurance.sampleCount
        memoryLeakSuspected=$Endurance.memoryLeakSuspected
    }

    if ($Svc.State -ne 'Running') { throw "Usluga nie dziala: $($Svc.State)" }
    if ($Status.heartbeat.deviceId -ne $DeviceBefore) { throw 'Device ID zmienil sie podczas testu.' }
    Start-Sleep -Seconds ([Math]::Max(60, $CheckMinutes * 60))
}

& "$PSScriptRoot\Get-SirkAgentEndurance.ps1"
$Collector = Join-Path $PSScriptRoot 'Collect-SirkAgent-TestBundle.ps1'
if (Test-Path $Collector) { & $Collector }
Write-Host 'Test dlugotrwaly zakonczony.' -ForegroundColor Green
Write-Host "Log: $Log"
