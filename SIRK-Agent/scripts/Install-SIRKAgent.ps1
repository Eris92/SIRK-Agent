#requires -Version 5.1
#requires -RunAsAdministrator

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceExe,

    [Parameter(Mandatory = $false, Position = 1)]
    [string]$WorkspaceHostSource = '',

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\SIRK\Agent",

    [Parameter(Mandatory = $false)]
    [string]$ExpectedSha256 = '',

    [Parameter(Mandatory = $false)]
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'SIRKAgent'
$displayName = 'SIRK Agent'

if (-not (Test-Path -LiteralPath $SourceExe -PathType Leaf)) {
    throw "Agent source executable was not found: $SourceExe"
}

if ($WorkspaceHostSource -and -not (Test-Path -LiteralPath $WorkspaceHostSource -PathType Leaf)) {
    throw "WorkspaceHost source executable was not found: $WorkspaceHostSource"
}

if ($ExpectedSha256 -and $ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'ExpectedSha256 must contain exactly 64 hexadecimal characters.'
}

$sourceFullPath = (Resolve-Path -LiteralPath $SourceExe).Path
$sourceDirectory = Split-Path -LiteralPath $sourceFullPath -Parent
$workspaceHostFullPath = if ($WorkspaceHostSource) { (Resolve-Path -LiteralPath $WorkspaceHostSource).Path } else { $null }
$workspaceHostDirectory = if ($workspaceHostFullPath) { Split-Path -LiteralPath $workspaceHostFullPath -Parent } else { $null }
$targetExe = Join-Path $InstallDirectory 'SIRK-Agent.exe'
$targetWorkspaceHost = Join-Path $InstallDirectory 'SIRK-WorkspaceHost.exe'

if ($ExpectedSha256) {
    $actualHash = (Get-FileHash -LiteralPath $sourceFullPath -Algorithm SHA256).Hash
    if ($actualHash -ne $ExpectedSha256.ToUpperInvariant()) {
        throw "SHA-256 mismatch. Expected: $ExpectedSha256 Actual: $actualHash"
    }
}

foreach ($binary in @($sourceFullPath, $workspaceHostFullPath) | Where-Object { $_ }) {
    $signature = Get-AuthenticodeSignature -LiteralPath $binary
    if ($signature.Status -notin @('Valid', 'NotSigned')) {
        throw "Invalid Authenticode status for $binary`: $($signature.Status)"
    }
}

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    $existingService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}

New-Item -Path $InstallDirectory -ItemType Directory -Force | Out-Null

# Well-known SIDs work on every Windows language version.
$systemSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
$administratorsSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
$usersSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')

$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($administratorsSid)
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$propagation = [System.Security.AccessControl.PropagationFlags]::None
$allow = [System.Security.AccessControl.AccessControlType]::Allow
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, 'FullControl', $inherit, $propagation, $allow)))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($administratorsSid, 'FullControl', $inherit, $propagation, $allow)))
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($usersSid, 'ReadAndExecute', $inherit, $propagation, $allow)))
Set-Acl -LiteralPath $InstallDirectory -AclObject $acl

# Framework-dependent deployments require the complete publish directories.
Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $InstallDirectory -Recurse -Force
if ($workspaceHostDirectory) {
    Copy-Item -Path (Join-Path $workspaceHostDirectory '*') -Destination $InstallDirectory -Recurse -Force
}

if (-not (Test-Path -LiteralPath $targetExe -PathType Leaf)) {
    throw "Installed agent executable was not found: $targetExe"
}
if ($workspaceHostFullPath -and -not (Test-Path -LiteralPath $targetWorkspaceHost -PathType Leaf)) {
    throw "Installed WorkspaceHost executable was not found: $targetWorkspaceHost"
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe config $serviceName binPath= ('"{0}"' -f $targetExe) start= delayed-auto DisplayName= $displayName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed with exit code $LASTEXITCODE" }
}
else {
    & sc.exe create $serviceName binPath= ('"{0}"' -f $targetExe) start= delayed-auto DisplayName= $displayName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE" }
}

& sc.exe description $serviceName 'SIRK Management Platform endpoint agent.' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE" }
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE" }
& sc.exe failureflag $serviceName 1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed with exit code $LASTEXITCODE" }

if (-not $NoStart) {
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
}

$installedService = Get-Service -Name $serviceName -ErrorAction Stop
[pscustomobject]@{
    ServiceName       = $serviceName
    Status            = $installedService.Status
    BinaryPath        = $targetExe
    WorkspaceHostPath = if (Test-Path -LiteralPath $targetWorkspaceHost) { $targetWorkspaceHost } else { $null }
    Sha256            = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
    Signature         = (Get-AuthenticodeSignature -LiteralPath $targetExe).Status
}
