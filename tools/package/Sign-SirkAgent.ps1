#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$Thumbprint,

    [Parameter(Mandatory)]
    [string]$PackagePath,

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$StoreLocation = 'CurrentUser'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PackagePath).Path
$certificatePath = "Cert:\$StoreLocation\My\$Thumbprint"
$certificate = Get-Item -LiteralPath $certificatePath -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'Code-signing certificate has no private key.' }
$now = Get-Date
if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) {
    throw 'Code-signing certificate is outside its validity period.'
}
if (-not ($certificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')) {
    throw 'Certificate is not valid for Code Signing.'
}

$files = Get-ChildItem -LiteralPath $root -File |
    Where-Object { $_.Extension -in '.exe', '.dll' } |
    Sort-Object FullName
if (-not $files) { throw 'No executable files were found to sign.' }

foreach ($file in $files) {
    $result = Set-AuthenticodeSignature -LiteralPath $file.FullName -Certificate $certificate `
        -HashAlgorithm SHA256
    if ($result.Status -ne 'Valid') {
        throw "Authenticode signing failed for $($file.Name): $($result.StatusMessage)"
    }
}

$verification = foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw "Authenticode verification failed for $($file.Name)."
    }
    [pscustomobject]@{
        path = $file.Name
        status = [string]$signature.Status
        signerThumbprint = $signature.SignerCertificate.Thumbprint
    }
}
$verification | ConvertTo-Json -Depth 3
