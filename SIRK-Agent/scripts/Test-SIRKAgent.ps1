#requires -Version 5.1
#requires -RunAsAdministrator

param(
    [string]$BundlePath = '.',
    [switch]$SkipInstall,
    [string]$ReportPath = "$env:TEMP\SIRK-Agent-Test-$((Get-Date).ToString('yyyyMMdd-HHmmss')).json",
    [string]$ScreenshotPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BundlePath -PathType Container)) {
    throw "Bundle directory was not found: $BundlePath"
}

$root = (Resolve-Path -LiteralPath $BundlePath).Path
$agent = Join-Path $root 'Agent\SIRK-Agent.exe'
$client = Join-Path $root 'Client\SIRK-Agent-Client.exe'
$workspaceHost = Join-Path $root 'WorkspaceHost\SIRK-WorkspaceHost.exe'
$installer = Join-Path $root 'Scripts\Install-SIRKAgent.ps1'

if ([string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    $ScreenshotPath = [IO.Path]::ChangeExtension($ReportPath, '.jpg')
}

foreach ($required in @($agent, $client, $workspaceHost, $installer)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing bundle component: $required"
    }
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'Microsoft .NET 8 Runtime x64 is required. Install it with: winget install Microsoft.DotNet.Runtime.8'
}

$runtimeInstalled = @(& $dotnet.Source --list-runtimes) -match '^Microsoft\.NETCore\.App 8\.'
if (-not $runtimeInstalled) {
    throw 'Microsoft .NET 8 Runtime x64 is required. Install it with: winget install Microsoft.DotNet.Runtime.8'
}

if (-not $SkipInstall) {
    & $installer $agent $workspaceHost
}

$service = Get-Service -Name 'SIRKAgent' -ErrorAction Stop
$service.WaitForStatus('Running', [TimeSpan]::FromSeconds(20))

function Invoke-AgentCommand {
    param(
        [string]$MessageType,
        [hashtable]$Payload = @{}
    )

    if ([string]::IsNullOrWhiteSpace($MessageType)) {
        throw 'MessageType is required.'
    }

    $payloadFile = Join-Path $env:TEMP ("sirk-payload-{0}.json" -f [guid]::NewGuid())
    try {
        $Payload | ConvertTo-Json -Depth 10 -Compress | Set-Content -LiteralPath $payloadFile -Encoding UTF8
        $raw = & $client $MessageType $env:COMPUTERNAME "test:$env:USERNAME" "@$payloadFile" 2>&1
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
    finally {
        Remove-Item -LiteralPath $payloadFile -Force -ErrorAction SilentlyContinue
    }
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
if ($workspaceCapabilities.result.capture.available -ne $true) { throw 'Workspace capture provider is not available.' }

$interactiveSession = @($workspaceCapabilities.result.session.sessions | Where-Object { $_.interactive -eq $true }) | Select-Object -First 1
if (-not $interactiveSession) {
    throw 'No active interactive Windows session is available for screenshot capture.'
}

$capturePayload = @{
    sessionId = [int]$interactiveSession.sessionId
    monitorId = 'primary'
    format = 'jpeg'
    quality = 60
    maxWidth = 1280
    maxHeight = 720
    includeCursor = $false
}

$capture = $null
$captureError = $null
$captureStarted = Get-Date

for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        Write-Host "Workspace capture attempt $attempt/3..."
        $capture = Invoke-AgentCommand -MessageType 'Workspace.CaptureFrame' -Payload $capturePayload
        $captureError = $null
        break
    }
    catch {
        $captureError = $_
        if ($attempt -lt 3 -and $_.Exception.Message -match 'capture_timeout|workspace_host_ipc_failed') {
            Write-Warning 'WorkspaceHost did not return the first frame. Waiting 5 seconds and retrying.'
            Start-Sleep -Seconds 5
            continue
        }
        throw
    }
}

if (-not $capture) {
    throw $captureError
}

$captureMilliseconds = [int]((Get-Date) - $captureStarted).TotalMilliseconds

if ($capture.result.contentType -ne 'image/jpeg' -or [string]::IsNullOrWhiteSpace($capture.result.frameBase64)) {
    throw 'Workspace.CaptureFrame did not return a JPEG frame.'
}

$frameBytes = [Convert]::FromBase64String($capture.result.frameBase64)
if ($frameBytes.Length -lt 1024) {
    throw "Captured JPEG is unexpectedly small: $($frameBytes.Length) bytes."
}

[IO.File]::WriteAllBytes($ScreenshotPath, $frameBytes)
$installedAgent = "$env:ProgramFiles\SIRK\Agent\SIRK-Agent.exe"
$installedHost = "$env:ProgramFiles\SIRK\Agent\SIRK-WorkspaceHost.exe"

$report = [ordered]@{
    TestedAtUtc = [DateTime]::UtcNow.ToString('o')
    ComputerName = $env:COMPUTERNAME
    User = "$env:USERDOMAIN\$env:USERNAME"
    Runtime = [ordered]@{
        DotNetPath = $dotnet.Source
        Installed = @(& $dotnet.Source --list-runtimes)
    }
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
        Screenshot = [ordered]@{
            Path = $ScreenshotPath
            Bytes = $frameBytes.Length
            Sha256 = (Get-FileHash -LiteralPath $ScreenshotPath -Algorithm SHA256).Hash
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
        WorkspaceHostHandshake = $true
        CaptureFrame = $true
    }
    Capture = [ordered]@{
        SessionId = [int]$interactiveSession.sessionId
        StationName = $interactiveSession.stationName
        Provider = $workspaceCapabilities.result.capture.executionProvider
        Milliseconds = $captureMilliseconds
        JpegBytes = $frameBytes.Length
        Quality = $capturePayload.quality
        MaxWidth = $capturePayload.maxWidth
        MaxHeight = $capturePayload.maxHeight
    }
    Workspace = $workspaceCapabilities.result
}

$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
Write-Host 'SIRK-Agent functional workspace test passed.' -ForegroundColor Green
Write-Host "Screenshot: $ScreenshotPath"
Write-Host "Report:     $ReportPath"
Write-Host "Capture:    $captureMilliseconds ms / $($frameBytes.Length) bytes"