#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK Agent",
    [string]$ServiceName = "SirkAgent",
    [string]$WatchdogServiceName = "SirkAgentWatchdog",
    [switch]$RemoveAgentData
)

$ErrorActionPreference = 'Stop'
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'SIRKAgentSession' -ErrorAction SilentlyContinue
Get-Process 'SirkAgent.Session' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

foreach ($key in @(
    'HKLM:\SOFTWARE\Google\Chrome\NativeMessagingHosts\pl.sirk.agent.browser',
    'HKLM:\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\pl.sirk.agent.browser'
)) {
    Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
}
foreach ($name in @($WatchdogServiceName, $ServiceName)) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $name -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        & sc.exe delete $name | Out-Null
        Start-Sleep -Seconds 2
    }
}

if (Test-Path -LiteralPath $InstallPath) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

if ($RemoveAgentData) {
    $dataPath = Join-Path $env:ProgramData 'SIRK\Agent'
    if (Test-Path -LiteralPath $dataPath) {
        Remove-Item -LiteralPath $dataPath -Recurse -Force
    }
}

Write-Host 'SIRK Agent odinstalowany.' -ForegroundColor Green
if (-not $RemoveAgentData) {
    Write-Host "Dane diagnostyczne zachowano w: $env:ProgramData\SIRK\Agent"
}
