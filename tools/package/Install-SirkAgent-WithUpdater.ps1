#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK\Agent",
    [string]$ServiceName = 'SirkAgent',
    [string]$WatchdogServiceName = 'SirkAgentWatchdog',
    [ValidateSet('preview','stable')]
    [string]$Channel = 'stable',
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$coreInstaller = Join-Path $root 'Install-SirkAgent.ps1'
$registerUpdater = Join-Path $root 'Register-SirkUpdater.ps1'
$dataRoot = Join-Path $env:ProgramData 'SIRK\Agent'
$heartbeatPath = Join-Path $dataRoot 'heartbeat-latest.json'

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

if (-not $NoStart) {
    Write-Host 'Waiting for SIRK Agent identity and heartbeat...' -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(90)
    $lastReason = 'heartbeat file has not been created'
    do {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $service) {
            $lastReason = "service $ServiceName does not exist"
        }
        elseif ($service.Status -ne 'Running') {
            $lastReason = "service $ServiceName is $($service.Status)"
            try { Start-Service -Name $ServiceName -ErrorAction Stop } catch {}
        }
        elseif (Test-Path -LiteralPath $heartbeatPath -PathType Leaf) {
            try {
                $heartbeat = Get-Content -LiteralPath $heartbeatPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $tenantId = [string]$heartbeat.tenantId
                $deviceId = [string]$heartbeat.deviceId
                if (-not [string]::IsNullOrWhiteSpace($tenantId) -and
                    -not [string]::IsNullOrWhiteSpace($deviceId)) {
                    Write-Host "Agent identity ready: tenant=$tenantId device=$deviceId" -ForegroundColor Green
                    $lastReason = $null
                    break
                }
                $lastReason = 'heartbeat does not contain tenantId/deviceId'
            }
            catch {
                $lastReason = 'heartbeat JSON is not ready: ' + $_.Exception.Message
            }
        }
        Start-Sleep -Milliseconds 750
    } while ((Get-Date) -lt $deadline)

    if ($lastReason) {
        $serviceState = Get-Service -Name $ServiceName,$WatchdogServiceName -ErrorAction SilentlyContinue |
            Select-Object Name,Status,StartType |
            Format-Table -AutoSize |
            Out-String
        $recentEvents = Get-WinEvent -FilterHashtable @{
                LogName = 'Application'
                StartTime = (Get-Date).AddMinutes(-10)
            } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'SirkAgent|SIRK Agent' } |
            Select-Object -First 20 TimeCreated,Id,LevelDisplayName,Message |
            Format-List |
            Out-String
        throw "SIRK Agent did not become ready for enrollment within 90 seconds: $lastReason`n$serviceState`n$recentEvents"
    }
}

& $registerUpdater `
    -InstallPath $InstallPath `
    -ServiceName $ServiceName `
    -WatchdogServiceName $WatchdogServiceName `
    -Channel $Channel

Write-Host 'SIRK_AGENT_INSTALLATION_WITH_UPDATER_OK' -ForegroundColor Green