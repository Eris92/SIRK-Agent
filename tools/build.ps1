[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64','win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$SelfContained,

    # Installs .NET 8 SDK with winget when dotnet is missing.
    [switch]$InstallDotNet
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root 'artifacts'
$Publish = Join-Path $Out "WorkspaceHost-$Runtime"
$PluginZip = Join-Path $Out 'MeshCentral-Workspace-Plugin-0.1.0.zip'
$Project = Join-Path $Root 'WorkspaceHost\WorkspaceHost.csproj'

function Resolve-DotNet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $defaultPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $defaultPath) {
        return $defaultPath
    }

    if (-not $InstallDotNet) {
        throw @"
Nie znaleziono .NET SDK.

Zainstaluj .NET 8 SDK poleceniem:
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements

Nastepnie zamknij i otworz PowerShell albo uruchom build z parametrem:
  .\tools\build.ps1 -Configuration $Configuration -Runtime $Runtime -InstallDotNet
"@
    }

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'Nie znaleziono winget. Zainstaluj recznie .NET 8 SDK i uruchom skrypt ponownie.'
    }

    Write-Host 'Instalowanie .NET 8 SDK...' -ForegroundColor Cyan
    & $winget.Source install --id Microsoft.DotNet.SDK.8 -e `
        --accept-package-agreements --accept-source-agreements

    if ($LASTEXITCODE -ne 0) {
        throw "Instalacja .NET 8 SDK zakonczyla sie kodem $LASTEXITCODE."
    }

    if (Test-Path $defaultPath) {
        return $defaultPath
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw 'SDK zostalo zainstalowane, ale dotnet.exe nie jest jeszcze widoczne. Otworz nowy PowerShell i uruchom build ponownie.'
}

if (-not (Test-Path $Project)) {
    throw "Nie znaleziono projektu: $Project"
}

$DotNet = Resolve-DotNet
Write-Host "dotnet: $DotNet" -ForegroundColor DarkGray
& $DotNet --version

Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Publish -ItemType Directory -Force | Out-Null

Write-Host 'Przywracanie pakietow...' -ForegroundColor Cyan
& $DotNet restore $Project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore zakonczyl sie kodem $LASTEXITCODE." }

Write-Host 'Budowanie WorkspaceHost...' -ForegroundColor Cyan
$selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
& $DotNet publish $Project `
    -c $Configuration `
    -r $Runtime `
    "--self-contained:$selfContainedValue" `
    -o $Publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish zakonczyl sie kodem $LASTEXITCODE." }

Write-Host 'Pakowanie pluginu...' -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $Root 'MeshCentral-Plugin\*') -DestinationPath $PluginZip -Force

Write-Host ''
Write-Host "WorkspaceHost: $Publish" -ForegroundColor Green
Write-Host "Plugin ZIP:    $PluginZip" -ForegroundColor Green
