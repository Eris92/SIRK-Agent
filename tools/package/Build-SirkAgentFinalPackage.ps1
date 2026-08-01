#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$Version = '1.0.15',
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$SigningThumbprint
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$artifactsRoot = Join-Path $RepositoryRoot 'artifacts'
$package = Join-Path $artifactsRoot "SIRK-Agent-$Version-final"
$expectedPrefix = $artifactsRoot.TrimEnd('\') + '\'
if (Test-Path -LiteralPath $package) {
    $resolved = (Resolve-Path -LiteralPath $package).Path
    if (-not $resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package output path is outside the artifacts directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Path $package -Force | Out-Null

$projects = @(
    'src/SirkAgent.Service/SirkAgent.Service.csproj',
    'src/SirkAgent.Report/SirkAgent.Report.csproj',
    'src/SirkAgent.Cli/SirkAgent.Cli.csproj',
    'src/SirkAgent.Session/SirkAgent.Session.csproj',
    'src/SirkAgent.BrowserHost/SirkAgent.BrowserHost.csproj',
    'src/SirkAgent.Watchdog/SirkAgent.Watchdog.csproj'
)
$commit = (& git -C $RepositoryRoot rev-parse --short=12 HEAD).Trim()
foreach ($project in $projects) {
    $outputPath = if ($project -eq 'src/SirkAgent.Session/SirkAgent.Session.csproj') {
        Join-Path $package 'Session'
    } else {
        $package
    }
    & dotnet publish (Join-Path $RepositoryRoot $project) -c Release -r win-x64 `
        --self-contained false --no-restore "-p:InformationalVersion=$Version+$commit" `
        -o $outputPath --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}

Copy-Item (Join-Path $RepositoryRoot 'tools\package\*.ps1') $package -Force
Copy-Item (Join-Path $RepositoryRoot 'browser-extension') `
    (Join-Path $package 'BrowserExtension') -Recurse -Force

& (Join-Path $RepositoryRoot 'tools\package\Sign-SirkAgent.ps1') `
    -PackagePath $package -Thumbprint $SigningThumbprint |
    Set-Content (Join-Path $package 'authenticode-verification.json') -Encoding UTF8

$files = Get-ChildItem $package -File -Recurse |
    Where-Object { $_.Extension -in '.exe', '.dll' } |
    Sort-Object Name |
    ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($package, $_.FullName); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    }
@{ files = $files } | ConvertTo-Json -Depth 5 |
    Set-Content (Join-Path $package 'integrity-manifest.json') -Encoding UTF8
@{
    product = 'SIRK Agent'
    version = $Version
    commit = $commit
    buildUtc = (Get-Date).ToUniversalTime().ToString('o')
    runtime = 'win-x64'
    deployment = 'framework-dependent'
    requiredRuntime = '.NET 8 x64'
    authenticodeSigner = 'CN=Sir-K Mini RDP Signing'
} | ConvertTo-Json | Set-Content (Join-Path $package 'build-manifest.json') -Encoding UTF8

$forbidden = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')
$found = $forbidden | Where-Object { Test-Path (Join-Path $package $_) }
if ($found) { throw "Package contains bundled .NET runtime files: $($found -join ', ')" }

$zip = Join-Path $artifactsRoot "SIRK-Agent-$Version-win-x64-framework-dependent-signed.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip `
    -CompressionLevel Optimal

[pscustomobject]@{
    package = $package
    zip = $zip
    zipBytes = (Get-Item $zip).Length
    files = (Get-ChildItem $package -File -Recurse).Count
    signed = @($files).Count
    frameworkDependent = $true
    commit = $commit
}
