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

    try {
        $json | & $AdapterPath 1> $outputFile 2> $errorFile
        $exitCode = $LASTEXITCODE
        $rawOutput = Get-Content -LiteralPath $outputFile -Raw
        $errorOutput = if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile -Raw } else { '' }

        [pscustomobject]@{
            ExitCode = $exitCode
            Json     = $rawOutput | ConvertFrom-Json
            StdErr   = $errorOutput
        }
    }
    finally {
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
    if ($ping.ExitCode -ne 0) {
        throw "MeshAdapter ping failed. ExitCode=$($ping.ExitCode) StdErr=$($ping.StdErr)"
    }

    if (-not $ping.Json.ok -or $ping.Json.result.message -ne 'pong') {
        throw "MeshAdapter did not return a valid pong response."
    }

    $statusRequest = $baseRequest.Clone()
    $statusRequest.messageType = 'System.GetStatus'
    $status = Invoke-AdapterRequest -Request $statusRequest

    if ($status.ExitCode -ne 0 -or -not $status.Json.ok -or $status.Json.result.status -ne 'running') {
        throw "MeshAdapter status request failed."
    }

    $blockedRequest = $baseRequest.Clone()
    $blockedRequest.messageType = 'Terminal.Execute'
    $blocked = Invoke-AdapterRequest -Request $blockedRequest

    if ($blocked.ExitCode -eq 0) {
        throw "MeshAdapter accepted a blocked messageType."
    }

    if ($blocked.Json.ok -ne $false -or $blocked.Json.error.code -ne 'invalid_request') {
        throw "MeshAdapter returned an unexpected blocked-command response."
    }

    Write-Host 'SIRK-MeshAdapter integration test passed.'
}
finally {
    if (-not $agent.HasExited) {
        Stop-Process -Id $agent.Id -Force -ErrorAction SilentlyContinue
        $agent.WaitForExit()
    }

    $agent.Dispose()
}
