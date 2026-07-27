param(
    [switch]$KeepServiceStopped
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    throw 'Uruchom skrypt jako administrator.'
}

$serviceName = 'SirkAgent'
$agentRoot = Join-Path $env:ProgramData 'SIRK\Agent'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$archiveRoot = Join-Path $agentRoot "ResetArchive\$timestamp"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceWasRunning = $service -and $service.Status -ne 'Stopped'

if ($serviceWasRunning) {
    Stop-Service -Name $serviceName -Force
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null

$resetFiles = @(
    'policy-state.bin',
    'quarantine-state.bin',
    'quarantine-state.json',
    'quarantine-status.json',
    'tamper-event-latest.json',
    'heartbeat-latest.json',
    'security-state.json'
)

$moved = [System.Collections.Generic.List[string]]::new()
foreach ($name in $resetFiles) {
    $source = Join-Path $agentRoot $name
    if (Test-Path -LiteralPath $source) {
        Move-Item -LiteralPath $source -Destination (Join-Path $archiveRoot $name) -Force
        $moved.Add($name)
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    resetUtc = (Get-Date).ToUniversalTime().ToString('o')
    computerName = $env:COMPUTERNAME
    serviceWasRunning = [bool]$serviceWasRunning
    preserved = @(
        'device-identity.bin',
        'evidence-events.jsonl',
        'evidence-state.bin',
        'TelemetryQueue',
        'agent-events.jsonl'
    )
    archived = $moved
    archivePath = $archiveRoot
    warning = 'TEST-ONLY reset. Production recovery must require a signed Recovery Policy.'
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $archiveRoot 'reset-manifest.json') -Encoding UTF8

if ($service -and -not $KeepServiceStopped) {
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
}

Write-Host ''
Write-Host 'Testowy stan SIRK Agent zostal zresetowany.' -ForegroundColor Green
Write-Host "Archiwum: $archiveRoot" -ForegroundColor Cyan
Write-Host 'Zachowano Device ID, Evidence Chain, Telemetry Queue i agent-events.jsonl.' -ForegroundColor Cyan
if ($service -and -not $KeepServiceStopped) {
    Write-Host 'Usluga SirkAgent zostala ponownie uruchomiona.' -ForegroundColor Green
}
