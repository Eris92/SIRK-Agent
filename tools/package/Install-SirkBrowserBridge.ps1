#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\SIRK Agent",
    [string]$ExtensionId = 'kmjplemahkjpfoephgcalhmipelkaion'
)

$ErrorActionPreference = 'Stop'
if ($ExtensionId -notmatch '^[a-p]{32}$') {
    throw 'Nieprawidlowy identyfikator rozszerzenia Chrome/Edge.'
}
$hostExe = Join-Path $InstallPath 'SirkAgent.BrowserHost.exe'
if (-not (Test-Path -LiteralPath $hostExe)) {
    throw "Brak natywnego hosta: $hostExe"
}
$manifestPath = Join-Path $InstallPath 'pl.sirk.agent.browser.json'
$manifest = @{
    name = 'pl.sirk.agent.browser'
    description = 'SIRK Agent policy-controlled browser bridge'
    path = $hostExe
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}
$manifest | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $manifestPath -Encoding UTF8

foreach ($key in @(
    'HKLM:\SOFTWARE\Google\Chrome\NativeMessagingHosts\pl.sirk.agent.browser',
    'HKLM:\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\pl.sirk.agent.browser'
)) {
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $manifestPath
}

Write-Host "SIRK Browser Bridge zarejestrowany dla rozszerzenia $ExtensionId." -ForegroundColor Green
