#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pipe = Get-Content (Join-Path $root 'src\SirkAgent.Service\InteractiveSessionPipe.cs') -Raw
$session = Get-Content (Join-Path $root 'src\SirkAgent.Session\Program.cs') -Raw

foreach ($required in @(
    'WaitNamedPipe',
    'PipeReady(sessionId, 100)',
    'WaitForSingleObject(process.Process, 0)',
    'GetExitCodeProcess',
    'session-startup-error.log'
)) {
    if ($pipe.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Session broker readiness contract missing: $required"
    }
}

if ($pipe -match 'internal static bool IsAvailable\(int sessionId\)\s*=>\s*Process\.GetProcessesByName') {
    throw 'Session broker readiness still relies only on process enumeration.'
}
if ($session -notmatch 'Lazy<ImageCodecInfo> JpegEncoder') {
    throw 'JPEG codec initialization is still eager during broker startup.'
}
if ($session -notmatch 'private static async Task<int> Main\((?:string\[\] args)?\)') {
    throw 'Session broker fatal startup guard is missing.'
}
if ($session -notmatch 'LogFatalStartup\(error\)') {
    throw 'Session broker fatal startup diagnostics are missing.'
}

Write-Host 'SESSION_BROKER_READINESS_CONTRACT_OK'
