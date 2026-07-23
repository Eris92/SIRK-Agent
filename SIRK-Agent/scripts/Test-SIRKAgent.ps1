#requires -Version 5.1
#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$BundlePath,

    [switch]$SkipInstall,

    [string]$ReportPath = "$env:TEMP\SIRK-Agent-Test-$((Get-Date).ToString('yyyyMMdd-HHmmss')).json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $BundlePath).Path
$agent = Join-Path $root 'Agent\SIRK-Agent.exe'
$client = Join-Path $root 'Client\SIRK-Agent.Client.exe'
$workspaceHost = Join-Path $root 'WorkspaceHost\SIRK-WorkspaceHost.exe'
$installer = Join-Path $root 'Scripts\Install-SIRKAgent.ps1'

foreach ($required in @($agent, $client, $workspaceHost, $installer)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing bundle component: $required"
    }
}

if (-not $SkipInstall) {
    & $installer -SourceExe $agent -WorkspaceHostSource $workspaceHost
}

$service = Get-Service -Name 'SIRKAgent' -ErrorAction Stop
$service.WaitForStatus('Running', [TimeSpan]::FromSeconds(20))

function Invoke-AgentCommand {
    param([Parameter(Mandatory)][string]$MessageType)

    $raw = & $client $MessageType $env:COMPUTERNAME "test:$env:USERNAME" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Client failed for $MessageType with exit code $LASTEXITCODE. Output: $raw"
    }

    $text = $raw -join [Environment]::NewLine
    $json = $text | ConvertFrom-Json
    if (-not $json.ok) {
        throw "Agent returned an error for $MessageType`: $($json.error.code) $($json.error.message)"
    }

    return $json
}

$ping = Invoke-AgentCommand -MessageType 'System.Ping'
$status = Invoke-AgentCommand -MessageType 'System.GetStatus'
$systemCapabilities = Invoke-AgentCommand -MessageType 'System.GetCapabilities'
$workspaceCapabilities = Invoke-AgentCommand -MessageType 'Workspace.GetCapabilities'

if ($ping.result.message -ne 'pong') { throw 'System.Ping did not return pong.' }
if ($status.result.status -ne 'running') { throw 'System.GetStatus did not return running.' }
if ($workspaceCapabilities.result.session.sessionZeroIsolation -ne $true) { throw 'Session 0 isolation is not enabled.' }
if ($workspaceCapabilities.result.session.rdsEnumerationAvailable -ne $true) { throw 'RDS session enumeration is not enabled.' }
if ($workspaceCapabilities.result.workspaceHost.installed -ne $true) { throw 'WorkspaceHost was not detected after installation.' }

$installedAgent = "$env:ProgramFiles\SIRK\Agent\SIRK-Agent.exe"
$installedHost = "$env:ProgramFiles\SIRK\Agent\SIRK-WorkspaceHost.exe"

$report = [ordered]@{
    TestedAtUtc = [DateTime]::UtcNow.ToString('o')
    ComputerName = $env:COMPUTERNAME
    User = "$env:USERDOMAIN\$env:USERNAME"
    Service = [ordered]@{
        Name = $service.Name
        Status = $service.Status.ToString()
        StartType = $service.StartType.ToString()
    }
    Files = [ordered]@{
        Agent = [ordered]@{
            Path = $installedAgent
            Sha256 = (Get-FileHash -LiteralPath $installedAgent -Algorithm SHA256).Hash
            Signature = (Get-AuthenticodeSignature -LiteralPath $installedAgent).Status.ToString()
        }
        WorkspaceHost = [ordered]@{
            Path = $installedHost
            Sha256 = (Get-FileHash -LiteralPath $installedHost -Algorithm SHA256).Hash
            Signature = (Get-AuthenticodeSignature -LiteralPath $installedHost).Status.ToString()
        }
    }
    Checks = [ordered]@{
        Ping = $true
        Status = $true
        SystemCapabilities = $true
        WorkspaceCapabilities = $true
        SessionZeroIsolation = $true
        RdsEnumeration = $true
        WorkspaceHostDetected = $true
    }
    Workspace = $workspaceCapabilities.result
}

$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
Write-Host "SIRK-Agent smoke test passed."
Write-Host "Report: $ReportPath"
$report
