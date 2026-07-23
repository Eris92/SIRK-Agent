[CmdletBinding()]
param(
    [string]$Repo = 'Eris92/MeshCentral-Workspace',
    [string]$Branch = 'develop',
    [string]$DownloadDirectory = "$PSScriptRoot\..\downloads",
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DownloadDirectory = [System.IO.Path]::GetFullPath($DownloadDirectory)

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'Brak GitHub CLI. Zainstaluj: winget install --id GitHub.cli -e'
}

& gh auth status | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI nie jest zalogowane. Uruchom: gh auth login'
}

$runId = (& gh run list --repo $Repo --branch $Branch --status success --limit 1 --json databaseId --jq '.[0].databaseId').Trim()
if ([string]::IsNullOrWhiteSpace($runId)) {
    throw "Nie znaleziono udanego workflow dla branch $Branch."
}

Write-Host "Run ID: $runId" -ForegroundColor Cyan
Remove-Item $DownloadDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item $DownloadDirectory -ItemType Directory -Force | Out-Null

& gh run download $runId --repo $Repo --dir $DownloadDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Pobieranie artefaktow zakonczone kodem $LASTEXITCODE."
}

$hostZip = Get-ChildItem $DownloadDirectory -Recurse -Filter 'WorkspaceHost-win-x64.zip' | Select-Object -First 1
if (-not $hostZip) {
    throw 'Nie znaleziono WorkspaceHost-win-x64.zip w pobranych artefaktach.'
}

$extract = Join-Path $DownloadDirectory 'WorkspaceHost-test'
Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $hostZip.FullName -DestinationPath $extract -Force

$exe = Get-ChildItem $extract -Recurse -Filter 'WorkspaceHost.exe' | Select-Object -First 1
if (-not $exe) {
    throw 'Nie znaleziono WorkspaceHost.exe po rozpakowaniu artefaktu.'
}

Write-Host "Testowanie: $($exe.FullName)" -ForegroundColor Cyan
$testScript = Join-Path $Root 'tools\test-workspacehost.ps1'
& $testScript -ExePath $exe.FullName -KeepRunning:$KeepRunning
if ($LASTEXITCODE -ne 0) {
    throw "Test WorkspaceHost zakonczony kodem $LASTEXITCODE."
}

Write-Host ''
Write-Host 'Gotowe: artefakt pobrany i heartbeat zweryfikowany.' -ForegroundColor Green
Write-Host "Pobrane pliki: $DownloadDirectory" -ForegroundColor Green
