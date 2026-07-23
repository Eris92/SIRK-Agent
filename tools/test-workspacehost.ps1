[CmdletBinding()]
param(
    [string]$ExePath = "$PSScriptRoot\..\artifacts\WorkspaceHost-win-x64\WorkspaceHost.exe",
    [int]$TimeoutSeconds = 20,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$ExePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path $ExePath)) {
    throw "Nie znaleziono WorkspaceHost.exe: $ExePath"
}

$process = $null
$pipe = $null
$reader = $null

try {
    Write-Host "Uruchamianie: $ExePath" -ForegroundColor Cyan
    $process = Start-Process -FilePath $ExePath -PassThru -WindowStyle Hidden

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        'SirK.MeshCentral.Workspace',
        [System.IO.Pipes.PipeDirection]::In,
        [System.IO.Pipes.PipeOptions]::None
    )

    Write-Host "Laczenie z Named Pipe..." -ForegroundColor Cyan
    $pipe.Connect($TimeoutSeconds * 1000)
    $reader = [System.IO.StreamReader]::new($pipe, [System.Text.Encoding]::UTF8)

    $readTask = $reader.ReadLineAsync()
    if (-not $readTask.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        throw "Nie otrzymano heartbeat w ciagu $TimeoutSeconds sekund."
    }

    $line = $readTask.Result
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw 'Odebrano pusty heartbeat.'
    }

    Write-Host "Heartbeat JSON: $line" -ForegroundColor DarkGray
    $heartbeat = $line | ConvertFrom-Json

    $required = @('type', 'version', 'pid', 'sessionId', 'user', 'desktop', 'uptimeSeconds')
    foreach ($name in $required) {
        if ($null -eq $heartbeat.PSObject.Properties[$name]) {
            throw "Heartbeat nie zawiera pola: $name"
        }
    }

    if ($heartbeat.type -ne 'heartbeat') {
        throw "Nieprawidlowy typ komunikatu: $($heartbeat.type)"
    }

    if ([int]$heartbeat.pid -ne $process.Id) {
        throw "PID heartbeat ($($heartbeat.pid)) nie zgadza sie z PID procesu ($($process.Id))."
    }

    Write-Host ''
    Write-Host 'WorkspaceHost test: OK' -ForegroundColor Green
    [pscustomobject]@{
        Status          = 'OK'
        Version         = $heartbeat.version
        PID             = $heartbeat.pid
        WindowsSession  = $heartbeat.sessionId
        User            = $heartbeat.user
        Desktop         = $heartbeat.desktop
        UptimeSeconds   = $heartbeat.uptimeSeconds
        LogPath         = 'C:\ProgramData\SirK\Workspace\Logs\workspace.log'
    } | Format-List
}
finally {
    if ($reader) { $reader.Dispose() }
    if ($pipe) { $pipe.Dispose() }

    if ($process -and -not $KeepRunning -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Write-Host "Zatrzymano WorkspaceHost PID $($process.Id)." -ForegroundColor DarkGray
    }
}
