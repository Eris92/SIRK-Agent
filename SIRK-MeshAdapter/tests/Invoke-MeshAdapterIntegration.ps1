[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AgentPath,

    [Parameter(Mandatory)]
    [string]$AdapterPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-AdapterRequest {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Request
    )

    $json = $Request | ConvertTo-Json -Depth 10 -Compress
    $outputFile = Join-Path $env:TEMP ("sirk-adapter-out-{0}.json" -f ([guid]::NewGuid()))
    $errorFile = Join-Path $env:TEMP ("sirk-adapter-err-{0}.log" -f ([guid]::NewGuid()))
    $previousErrorActionPreference = $ErrorActionPreference
    $nativePreferenceExists = $null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)
    $previousNativePreference = if ($nativePreferenceExists) { $PSNativeCommandUseErrorActionPreference } else { $null }

    try {
        $ErrorActionPreference = 'Continue'
        if ($nativePreferenceExists) {
            $PSNativeCommandUseErrorActionPreference = $false
        }

        $json | & $AdapterPath 1> $outputFile 2> $errorFile
        $exitCode = $LASTEXITCODE
        $global:LASTEXITCODE = 0

        $rawOutput = if (Test-Path -LiteralPath $outputFile) { Get-Content -LiteralPath $outputFile -Raw } else { '' }
        $errorOutput = if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile -Raw } else { '' }

        if ([string]::IsNullOrWhiteSpace($rawOutput)) {
            throw "SIRK-MeshAdapter returned no JSON. ExitCode=$exitCode StdErr=$errorOutput"
        }

        [pscustomobject]@{
            ExitCode = $exitCode
            Json     = $rawOutput | ConvertFrom-Json
            StdErr   = $errorOutput
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($nativePreferenceExists) {
            $PSNativeCommandUseErrorActionPreference = $previousNativePreference
        }
        $global:LASTEXITCODE = 0
        Remove-Item -LiteralPath $outputFile, $errorFile -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $AgentPath -PathType Leaf)) {
    throw "Agent not found: $AgentPath"
}

if (-not (Test-Path -LiteralPath $AdapterPath -PathType Leaf)) {
    throw "Adapter not found: $AdapterPath"
}

$agent = Start-Process -FilePath $AgentPath -PassThru -WindowStyle Hidden

try {
    Start-Sleep -Seconds 2

    if ($agent.HasExited) {
        throw "SIRK-Agent exited before integration test. ExitCode=$($agent.ExitCode)"
    }

    $baseRequest = @{
        messageType = 'System.Ping'
        deviceId    = $env:COMPUTERNAME
        operatorId  = 'ci:mesh-adapter'
        payload     = @{}
    }

    $ping = Invoke-AdapterRequest -Request $baseRequest
    if ($ping.ExitCode -ne 0 -or -not $ping.Json.ok -or $ping.Json.result.message -ne 'pong') {
        throw "MeshAdapter ping failed. ExitCode=$($ping.ExitCode) StdErr=$($ping.StdErr)"
    }

    $statusRequest = $baseRequest.Clone()
    $statusRequest.messageType = 'System.GetStatus'
    $status = Invoke-AdapterRequest -Request $statusRequest
    if ($status.ExitCode -ne 0 -or -not $status.Json.ok -or $status.Json.result.status -ne 'running') {
        throw 'MeshAdapter status request failed.'
    }

    $workspaceRequest = $baseRequest.Clone()
    $workspaceRequest.messageType = 'Workspace.GetCapabilities'
    $workspace = Invoke-AdapterRequest -Request $workspaceRequest
    if ($workspace.ExitCode -ne 0 -or -not $workspace.Json.ok -or $workspace.Json.result.module -ne 'Workspace') {
        throw 'MeshAdapter workspace capabilities request failed.'
    }
    if ($workspace.Json.result.capabilities -notcontains 'Workspace.CaptureFrame') {
        throw 'Workspace.CaptureFrame is missing from the capability report.'
    }
    if ($workspace.Json.result.session.sessionZeroIsolation -ne $true) {
        throw 'Workspace session zero isolation is not reported.'
    }
    if ($workspace.Json.result.session.rdsEnumerationAvailable -ne $true) {
        throw 'Workspace RDS session enumeration is not reported.'
    }

    $invalidCaptureRequest = $baseRequest.Clone()
    $invalidCaptureRequest.messageType = 'Workspace.CaptureFrame'
    $invalidCaptureRequest.payload = @{
        sessionId = 1
        format = 'png'
        quality = 70
        maxWidth = 1920
        maxHeight = 1080
        monitorId = 'primary'
        includeCursor = $true
    }
    $invalidCapture = Invoke-AdapterRequest -Request $invalidCaptureRequest
    if ($invalidCapture.Json.ok -ne $false -or $invalidCapture.Json.error.code -ne 'invalid_payload') {
        throw 'Workspace.CaptureFrame accepted an invalid format.'
    }

    $sessionZeroRequest = $baseRequest.Clone()
    $sessionZeroRequest.messageType = 'Workspace.CaptureFrame'
    $sessionZeroRequest.payload = @{
        sessionId = 0
        format = 'jpeg'
        quality = 70
        maxWidth = 1920
        maxHeight = 1080
        monitorId = 'primary'
        includeCursor = $true
    }
    $sessionZero = Invoke-AdapterRequest -Request $sessionZeroRequest
    if ($sessionZero.Json.ok -ne $false -or $sessionZero.Json.error.code -ne 'session_zero_blocked') {
        throw 'Workspace.CaptureFrame did not block Windows Session 0.'
    }

    $interactiveSession = @($workspace.Json.result.session.sessions | Where-Object { $_.interactive -eq $true }) | Select-Object -First 1
    if ($null -ne $interactiveSession) {
        $captureRequest = $baseRequest.Clone()
        $captureRequest.messageType = 'Workspace.CaptureFrame'
        $captureRequest.payload = @{
            sessionId = [int]$interactiveSession.sessionId
            format = 'jpeg'
            quality = 70
            maxWidth = 1920
            maxHeight = 1080
            monitorId = 'primary'
            includeCursor = $true
        }
        $capture = Invoke-AdapterRequest -Request $captureRequest
        if ($capture.Json.ok -ne $false -or $capture.Json.error.code -ne 'capture_provider_unavailable') {
            throw 'Workspace.CaptureFrame did not fail safely when the provider was unavailable.'
        }
    }
    else {
        $captureRequest = $baseRequest.Clone()
        $captureRequest.messageType = 'Workspace.CaptureFrame'
        $captureRequest.payload = @{
            sessionId = 1
            format = 'jpeg'
            quality = 70
            maxWidth = 1920
            maxHeight = 1080
            monitorId = 'primary'
            includeCursor = $true
        }
        $capture = Invoke-AdapterRequest -Request $captureRequest
        if ($capture.Json.ok -ne $false -or $capture.Json.error.code -ne 'interactive_session_unavailable') {
            throw 'Workspace.CaptureFrame did not reject a missing interactive session.'
        }
    }

    $blockedRequest = $baseRequest.Clone()
    $blockedRequest.messageType = 'Terminal.Execute'
    $blocked = Invoke-AdapterRequest -Request $blockedRequest
    if ($blocked.ExitCode -eq 0 -or $blocked.Json.ok -ne $false -or $blocked.Json.error.code -ne 'invalid_request') {
        throw 'MeshAdapter accepted a blocked messageType.'
    }

    $global:LASTEXITCODE = 0
    Write-Host 'SIRK-MeshAdapter integration test passed.'
}
finally {
    if (-not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue
        $agent.WaitForExit()
    }

    $agent.Dispose()
    $global:LASTEXITCODE = 0
}
