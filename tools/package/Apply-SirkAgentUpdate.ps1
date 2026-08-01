#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StagedPath,
    [string]$InstallPath = "$env:ProgramFiles\SIRK\Agent",
    [string]$ServiceName = "SirkAgent",
    [string]$VerifierPath
)

$ErrorActionPreference = 'Stop'
$staged = (Resolve-Path -LiteralPath $StagedPath).Path
$stagedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'SIRK\Agent\Updates\Staged')) + [IO.Path]::DirectorySeparatorChar
if (-not ($staged + [IO.Path]::DirectorySeparatorChar).StartsWith($stagedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "StagedPath must be below $stagedRoot"
}
$install = (Resolve-Path -LiteralPath $InstallPath).Path
$cli = if ($VerifierPath) { (Resolve-Path -LiteralPath $VerifierPath).Path } else { Join-Path $install 'sirkctl.exe' }
$manifestPath = Join-Path $staged 'update-manifest.json'
if (-not (Test-Path -LiteralPath $cli) -or -not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Installed verifier or staged update manifest is missing.'
}

& $cli verify-update --package $staged
if ($LASTEXITCODE -ne 0) { throw 'Signed update package verification failed.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$backupRoot = Join-Path $env:ProgramData ('SIRK\Agent\Updates\Backup\' + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Get-ChildItem -LiteralPath $install -File | Copy-Item -Destination $backupRoot -Force

$service = Get-Service -Name $ServiceName -ErrorAction Stop
$wasRunning = $service.Status -eq 'Running'
try {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    foreach ($entry in $manifest.files) {
        $relative = [string]$entry.path
        $source = [IO.Path]::GetFullPath((Join-Path $staged ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)))
        $destination = [IO.Path]::GetFullPath((Join-Path $install ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)))
        if (-not $source.StartsWith($stagedRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not $destination.StartsWith($install + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe update path: $relative"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    & (Join-Path $install 'sirkctl.exe') verify-integrity
    if ($LASTEXITCODE -ne 0) { throw 'Updated runtime failed integrity verification.' }
}
catch {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $install -File | Remove-Item -Force
    Get-ChildItem -LiteralPath $backupRoot -File | Copy-Item -Destination $install -Force
    if ($wasRunning) {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    }
    throw
}

[pscustomobject]@{
    ok = $true
    code = 'UPDATE_APPLIED'
    version = [string]$manifest.version
    installPath = $install
    backupPath = $backupRoot
    serviceStatus = (Get-Service -Name $ServiceName).Status.ToString()
    protectedStatePath = (Join-Path $env:ProgramData 'SIRK\Agent')
} | ConvertTo-Json
