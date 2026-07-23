#requires -Version 5.1
#requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$SourceExe,

    [ValidateScript({ -not $_ -or (Test-Path -LiteralPath $_ -PathType Leaf) })]
    [string]$WorkspaceHostSource,

    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\SIRK\Agent",

    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256,

    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'SIRKAgent'
$displayName = 'SIRK Agent'
$targetExe = Join-Path $InstallDirectory 'SIRK-Agent.exe'
$targetWorkspaceHost = Join-Path $InstallDirectory 'SIRK-WorkspaceHost.exe'
$sourceFullPath = (Resolve-Path -LiteralPath $SourceExe).Path
$workspaceHostFullPath = if ($WorkspaceHostSource) { (Resolve-Path -LiteralPath $WorkspaceHostSource).Path } else { $null }

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

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
}

if ($PSCmdlet.ShouldProcess($InstallDirectory, 'Install SIRK Agent')) {
    New-Item -Path $InstallDirectory -ItemType Directory -Force | Out-Null

    $acl = Get-Acl -LiteralPath $InstallDirectory
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) {
        [void]$acl.RemoveAccessRuleAll($rule)
    }

    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow

    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', $inherit, $propagation, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'FullControl', $inherit, $propagation, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Users', 'ReadAndExecute', $inherit, $propagation, $allow)))
    Set-Acl -LiteralPath $InstallDirectory -AclObject $acl

    Copy-Item -LiteralPath $sourceFullPath -Destination $targetExe -Force
    if ($workspaceHostFullPath) {
        Copy-Item -LiteralPath $workspaceHostFullPath -Destination $targetWorkspaceHost -Force
    }

    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        & sc.exe config $serviceName binPath= ('"{0}"' -f $targetExe) start= delayed-auto DisplayName= $displayName | Out-Null
    }
    else {
        & sc.exe create $serviceName binPath= ('"{0}"' -f $targetExe) start= delayed-auto DisplayName= $displayName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe create failed with exit code $LASTEXITCODE"
        }
    }

    & sc.exe description $serviceName 'SIRK Management Platform endpoint agent.' | Out-Null
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    & sc.exe failureflag $serviceName 1 | Out-Null

    if (-not $NoStart) {
        Start-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    }

    [pscustomobject]@{
        ServiceName        = $serviceName
        Status             = (Get-Service -Name $serviceName).Status
        BinaryPath         = $targetExe
        WorkspaceHostPath  = if (Test-Path -LiteralPath $targetWorkspaceHost) { $targetWorkspaceHost } else { $null }
        Sha256             = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
        Signature          = (Get-AuthenticodeSignature -LiteralPath $targetExe).Status
    }
}
