#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceFiles = Get-ChildItem (Join-Path $root 'src') -Recurse -File -Include *.cs
$legacy = $sourceFiles | Select-String -SimpleMatch '/api/agent/v1/'
if ($legacy) {
    $legacy | ForEach-Object { Write-Error $_ }
    throw 'Legacy Agent management route remains in product source.'
}

$management = Get-Content (Join-Path $root 'src\SirkAgent.Service\ManagementWorker.cs') -Raw
foreach ($required in @(
    '/api/v1/agent/checkin',
    'SynchronizeTrustedPolicyKeys',
    'Portal attempted to replace an established trusted policy key set',
    'if (current.Length == 0)',
    'AtomicFile.WriteJson(path, new TrustedKeyDocument(normalized), _json)',
    'PublicKeysEqual',
    'TrustedPolicyKeys',
    'credential with { Endpoint = endpoint.AbsoluteUri }'
)) {
    if (-not $management.Contains($required)) {
        throw "Canonical Agent management contract is missing: $required"
    }
}

$desktop = Get-Content (Join-Path $root 'src\SirkAgent.Service\DesktopStreamWorker.cs') -Raw
foreach ($required in @(
    '/api/v1/agent/desktop/stream',
    '/api/v1/agent/desktop/control'
)) {
    if (-not $desktop.Contains($required)) {
        throw "Canonical desktop Agent endpoint is missing: $required"
    }
}

$setup = Get-Content (Join-Path $root 'src\SirkAgent.Setup\Program.cs') -Raw
if (-not $setup.Contains('/api/v1/agent/enroll')) {
    throw 'Setup still enrolls through a non-canonical route.'
}

Write-Host 'CANONICAL_AGENT_MANAGEMENT_V1_CONTRACT_OK'
