#requires -Version 5.1
#requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\SIRK\Agent",

    [switch]$KeepFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'SIRKAgent'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($service -and $PSCmdlet.ShouldProcess($serviceName, 'Remove Windows service')) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete failed with exit code $LASTEXITCODE"
    }
}

if (-not $KeepFiles -and (Test-Path -LiteralPath $InstallDirectory)) {
    if ($PSCmdlet.ShouldProcess($InstallDirectory, 'Remove SIRK Agent files')) {
        Remove-Item -LiteralPath $InstallDirectory -Recurse -Force

        $parent = Split-Path -Parent $InstallDirectory
        if ((Test-Path -LiteralPath $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $parent -Force
        }
    }
}

[pscustomobject]@{
    ServiceRemoved = -not [bool](Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
    FilesKept      = [bool]$KeepFiles
    InstallPath    = $InstallDirectory
}
