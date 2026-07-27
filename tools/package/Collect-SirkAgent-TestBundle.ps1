#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'SIRK-Agent-TestBundles'),
    [switch]$SkipReport
)

$ErrorActionPreference = 'Stop'
$packagePath = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentData = Join-Path $env:ProgramData 'SIRK\Agent'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bundleRoot = Join-Path $env:TEMP "SIRK-Agent-TestBundle-$timestamp"
$zipPath = Join-Path $OutputDirectory "SIRK-Agent-TestBundle-$env:COMPUTERNAME-$timestamp.zip"

New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

try {
    if (-not $SkipReport) {
        $reportExe = Join-Path $packagePath 'SirkAgent.Report.exe'
        if (Test-Path -LiteralPath $reportExe) {
            & $reportExe --no-open
        }
    }

    if (Test-Path -LiteralPath $agentData) {
        Copy-Item -LiteralPath $agentData -Destination (Join-Path $bundleRoot 'AgentData') -Recurse -Force
    }

    $service = Get-CimInstance Win32_Service -Filter "Name='SirkAgent'" -ErrorAction SilentlyContinue
    $service | Select-Object Name, DisplayName, State, StartMode, StartName, PathName, ExitCode |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bundleRoot 'service.json') -Encoding UTF8

    [pscustomobject]@{
        schemaVersion = 1
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        computerName = $env:COMPUTERNAME
        user = "$env:USERDOMAIN\$env:USERNAME"
        os = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture)
        dotnetRuntimes = @(& dotnet --list-runtimes 2>$null)
        packagePath = $packagePath
        agentDataPath = $agentData
        serviceInstalled = [bool]$service
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundleRoot 'bundle-manifest.json') -Encoding UTF8

    Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddDays(-2) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'SirkAgent|\.NET Runtime|Application Error' -or $_.Message -match 'SIRK Agent|SirkAgent' } |
        Select-Object -First 300 TimeCreated, Id, LevelDisplayName, ProviderName, Message |
        Export-Csv -LiteralPath (Join-Path $bundleRoot 'windows-events.csv') -NoTypeInformation -Encoding UTF8

    Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
    Write-Host "TestBundle gotowy: $zipPath" -ForegroundColor Green
    Write-Output $zipPath
}
finally {
    Remove-Item -LiteralPath $bundleRoot -Recurse -Force -ErrorAction SilentlyContinue
}
