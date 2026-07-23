[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',

    # Zachowane dla zgodnosci ze starym poleceniem. C++ nie wymaga .NET runtime.
    [switch]$SelfContained,
    [switch]$InstallDotNet
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root 'artifacts'
$Build = Join-Path $Root "build\$Runtime"
$Publish = Join-Path $Out "WorkspaceHost-$Runtime"
$PluginZip = Join-Path $Out 'MeshCentral-Workspace-Plugin-0.2.0.zip'
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

    foreach ($path in $paths) {
        if (Test-Path $path) { return $path }
    }

    throw @"
Nie znaleziono CMake.

Zainstaluj Visual Studio 2022 Build Tools z workloadem C++:
  winget install --id Microsoft.VisualStudio.2022.BuildTools -e --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"

Nastepnie otworz nowy PowerShell i uruchom build ponownie.
"@
}

if (-not (Test-Path (Join-Path $Project 'CMakeLists.txt'))) {
    throw "Nie znaleziono projektu C++: $Project\CMakeLists.txt"
}

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

Write-Host 'Budowanie WorkspaceHost C++...' -ForegroundColor Cyan
& $CMake --build $Build --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "CMake build zakonczyl sie kodem $LASTEXITCODE." }

$Exe = Join-Path $Build "$Configuration\WorkspaceHost.exe"
if (-not (Test-Path $Exe)) {
    throw "Nie znaleziono wyniku kompilacji: $Exe"
}

Copy-Item $Exe (Join-Path $Publish 'WorkspaceHost.exe') -Force

Write-Host 'Pakowanie pluginu...' -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $Root 'MeshCentral-Plugin\*') -DestinationPath $PluginZip -Force

Write-Host ''
Write-Host "WorkspaceHost: $Publish" -ForegroundColor Green
Write-Host "Plugin ZIP:    $PluginZip" -ForegroundColor Green
