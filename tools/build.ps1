[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root 'artifacts'
$Publish = Join-Path $Out "WorkspaceHost-$Runtime"
$PluginZip = Join-Path $Out 'MeshCentral-Workspace-Plugin-0.1.0.zip'

Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Publish -ItemType Directory -Force | Out-Null

dotnet restore (Join-Path $Root 'WorkspaceHost\WorkspaceHost.csproj')
dotnet publish (Join-Path $Root 'WorkspaceHost\WorkspaceHost.csproj') `
    -c $Configuration -r $Runtime --self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant()) `
    -o $Publish

Compress-Archive -Path (Join-Path $Root 'MeshCentral-Plugin\*') -DestinationPath $PluginZip -Force

Write-Host "WorkspaceHost: $Publish"
Write-Host "Plugin ZIP:    $PluginZip"
