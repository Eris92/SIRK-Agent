#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK Agent",
    [string]$ServiceName = "SirkAgent",
    [switch]$RemoveAgentData
)

$ErrorActionPreference = 'Stop'
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'SIRKAgentSession' -ErrorAction SilentlyContinue
Get-Process 'SirkAgent.Session' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
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
