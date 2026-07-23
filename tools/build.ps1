[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$SelfContained,
    [switch]$InstallDotNet
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root 'artifacts'
$Build = Join-Path $Root "build\$Runtime"
$Publish = Join-Path $Out "WorkspaceHost-$Runtime"
$PluginConfigPath = Join-Path $Root 'MeshCentral-Plugin\config.json'
if (-not (Test-Path $PluginConfigPath)) { throw "Nie znaleziono konfiguracji pluginu: $PluginConfigPath" }
$PluginVersion = [string]((Get-Content $PluginConfigPath -Raw | ConvertFrom-Json).version)
if ([string]::IsNullOrWhiteSpace($PluginVersion)) { throw 'Brak wersji pluginu w config.json.' }
$PluginZip = Join-Path $Out "MeshCentral-Workspace-Plugin-$PluginVersion.zip"
$Project = Join-Path $Root 'WorkspaceHost'

function Resolve-CMake {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $paths = @(
        "$env:ProgramFiles\CMake\bin\cmake.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    )
    foreach ($path in $paths) { if (Test-Path $path) { return $path } }
    throw 'Nie znaleziono CMake. Lokalny build jest opcjonalny; uzyj GitHub Actions.'
}

if (-not (Test-Path (Join-Path $Project 'CMakeLists.txt'))) { throw "Nie znaleziono projektu C++: $Project\CMakeLists.txt" }
if (-not (Test-Path (Join-Path $Root 'MeshCentral-Plugin\workspace.js'))) { throw 'Brak wymaganego entrypointu MeshCentral-Plugin\workspace.js.' }

$CMake = Resolve-CMake
$Architecture = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
Write-Host "cmake: $CMake" -ForegroundColor DarkGray
& $CMake --version

Remove-Item $Build -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Build -ItemType Directory -Force | Out-Null
New-Item $Publish -ItemType Directory -Force | Out-Null

Write-Host 'Konfigurowanie projektu C++...' -ForegroundColor Cyan
& $CMake -S $Project -B $Build -A $Architecture
if ($LASTEXITCODE -ne 0) { throw "CMake configure zakonczyl sie kodem $LASTEXITCODE." }

Write-Host 'Budowanie WorkspaceHost i WorkspaceCapture C++...' -ForegroundColor Cyan
& $CMake --build $Build --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "CMake build zakonczyl sie kodem $LASTEXITCODE." }

$HostExe = Join-Path $Build "$Configuration\WorkspaceHost.exe"
$CaptureExe = Join-Path $Build "$Configuration\WorkspaceCapture.exe"
if (-not (Test-Path $HostExe)) { throw "Nie znaleziono wyniku kompilacji: $HostExe" }
if (-not (Test-Path $CaptureExe)) { throw "Nie znaleziono wyniku kompilacji: $CaptureExe" }
Copy-Item $HostExe (Join-Path $Publish 'WorkspaceHost.exe') -Force
Copy-Item $CaptureExe (Join-Path $Publish 'WorkspaceCapture.exe') -Force

Write-Host "Pakowanie pluginu $PluginVersion..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $Root 'MeshCentral-Plugin\*') -DestinationPath $PluginZip -Force

Write-Host ''
Write-Host "Workspace runtime: $Publish" -ForegroundColor Green
Write-Host "Plugin ZIP:       $PluginZip" -ForegroundColor Green
