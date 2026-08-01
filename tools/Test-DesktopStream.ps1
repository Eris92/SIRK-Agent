#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PortalUrl,
    [Parameter(Mandatory)][string]$PortalUser,
    [Parameter(Mandatory)][string]$PortalPassword,
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$DeviceId,
    [ValidateRange(5, 120)][int]$DurationSeconds = 12,
    [ValidateRange(0, 30)][int]$WarmupSeconds = 3,
    [ValidateRange(0, 65535)][int]$SessionId = 2,
    [ValidateRange(640, 1920)][int]$MaxWidth = 1920,
    [ValidateRange(25, 80)][int]$Quality = 72,
    [ValidateRange(300, 8000)][int]$TargetKbps = 1000,
    [switch]$GenerateWindowMotion
)

$ErrorActionPreference = 'Stop'
$portal = $PortalUrl.TrimEnd('/')
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$loginBody = 'username={0}&password={1}' -f
    [uri]::EscapeDataString($PortalUser), [uri]::EscapeDataString($PortalPassword)
Invoke-WebRequest -Uri "$portal/api/auth/login" -Method Post `
    -ContentType 'application/x-www-form-urlencoded' -Body $loginBody `
    -WebSession $session -SkipCertificateCheck | Out-Null
$bootstrap = Invoke-RestMethod -Uri "$portal/api/bootstrap" -WebSession $session -SkipCertificateCheck
$profileBody = @{
    tenantId = $TenantId
    deviceId = $DeviceId
    input = @{
        action = 'streamProfile'
        sessionId = $SessionId
        monitorIndex = 0
        maxWidth = $MaxWidth
        quality = $Quality
        targetKbps = $TargetKbps
    }
} | ConvertTo-Json -Depth 4
Invoke-WebRequest -Uri "$portal/api/agent-desktop/input" -Method Post `
    -ContentType 'application/json' -Headers @{ 'X-SIRK-CSRF' = $bootstrap.csrfToken } `
    -Body $profileBody -WebSession $session -SkipCertificateCheck | Out-Null

$motion = $null
if ($GenerateWindowMotion) {
    $motion = Start-Job -ArgumentList ($DurationSeconds + $WarmupSeconds) -ScriptBlock {
        param($MotionSeconds)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SirkDesktopBenchmarkWindow {
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
}
'@
        $process = Start-Process notepad -PassThru
        try {
            for ($attempt = 0; $attempt -lt 50 -and $process.MainWindowHandle -eq 0; $attempt++) {
                Start-Sleep -Milliseconds 100
                $process.Refresh()
            }
            for ($index = 0; $index -lt ($MotionSeconds * 63); $index++) {
                $x = 80 + [int](420 * (0.5 + 0.5 * [Math]::Sin($index / 18.0)))
                $y = 80 + [int](220 * (0.5 + 0.5 * [Math]::Cos($index / 23.0)))
                [SirkDesktopBenchmarkWindow]::MoveWindow(
                    $process.MainWindowHandle, $x, $y, 900, 600, $true) | Out-Null
                Start-Sleep -Milliseconds 16
            }
        } finally {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    $sequence = 0L
    $samples = [Collections.Generic.List[object]]::new()
    $benchmarkOrigin = [DateTimeOffset]::UtcNow
    $started = $benchmarkOrigin.AddSeconds($WarmupSeconds)
    $deadline = $started.AddSeconds($DurationSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(1, [int]($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
        $waitMilliseconds = [Math]::Min(1000, $remaining)
        $uri = "$portal/api/agent-desktop/frame?tenantId=$([uri]::EscapeDataString($TenantId))" +
            "&deviceId=$([uri]::EscapeDataString($DeviceId))&after=$sequence" +
            "&waitMilliseconds=$waitMilliseconds"
        $response = Invoke-WebRequest -Uri $uri -WebSession $session -SkipCertificateCheck
        if ($response.StatusCode -ne 200) { continue }
        $newSequence = [long]$response.Headers['X-SIRK-Sequence'][0]
        $encoded = $response.Headers['X-SIRK-Metadata'][0]
        $metadata = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)) |
            ConvertFrom-Json
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $atlasWidth = 0
        $atlasHeight = 0
        foreach ($patch in @($metadata.patches)) {
            $atlasWidth = [Math]::Max($atlasWidth, [int]$patch.atlasX + [int]$patch.atlasWidth)
            $atlasHeight = [Math]::Max($atlasHeight, [int]$patch.atlasY + [int]$patch.atlasHeight)
        }
        if ([DateTimeOffset]::UtcNow -lt $started) {
            $sequence = $newSequence
            continue
        }
        $samples.Add([pscustomobject]@{
            Sequence = $newSequence
            Bytes = $response.RawContentLength
            Capture = [double]$metadata.captureMilliseconds
            Encode = [double]$metadata.encodeMilliseconds
            Age = [Math]::Max(0, $now - [long]$metadata.capturedAtUnixMilliseconds)
            Full = [bool]$metadata.fullFrame
            Patches = @($metadata.patches).Count
            Moves = @($metadata.moves).Count
            AtlasWidth = $atlasWidth
            AtlasHeight = $atlasHeight
            AtlasPixels = $atlasWidth * $atlasHeight
            Backend = $metadata.captureBackend
        })
        $sequence = $newSequence
    }
    $streamEnded = [DateTimeOffset]::UtcNow
} finally {
    if ($motion) {
        Wait-Job $motion -Timeout 5 | Out-Null
        Remove-Job $motion -Force
    }
}

function Get-Percentile([object[]]$Values, [double]$Percentile) {
    $sorted = @($Values | Sort-Object)
    if (-not $sorted.Count) { return 0 }
    return $sorted[[Math]::Min($sorted.Count - 1,
        [Math]::Floor(($sorted.Count - 1) * $Percentile))]
}

$elapsed = [Math]::Max(0.001, ($streamEnded - $started).TotalSeconds)
$totalBytes = ($samples | Measure-Object Bytes -Sum).Sum
[pscustomobject]@{
    Frames = $samples.Count
    FPS = [Math]::Round($samples.Count / $elapsed, 2)
    MegabitsPerSecond = [Math]::Round($totalBytes * 8 / $elapsed / 1000000, 3)
    TotalMB = [Math]::Round($totalBytes / 1MB, 2)
    CaptureP50 = [Math]::Round((Get-Percentile $samples.Capture 0.5), 2)
    CaptureP95 = [Math]::Round((Get-Percentile $samples.Capture 0.95), 2)
    EncodeP50 = [Math]::Round((Get-Percentile $samples.Encode 0.5), 2)
    EncodeP95 = [Math]::Round((Get-Percentile $samples.Encode 0.95), 2)
    DeltaEncodeP50 = [Math]::Round((Get-Percentile @($samples | Where-Object { -not $_.Full }).Encode 0.5), 2)
    FullEncodeP50 = [Math]::Round((Get-Percentile @($samples | Where-Object Full).Encode 0.5), 2)
    AgeP50 = [Math]::Round((Get-Percentile $samples.Age 0.5), 2)
    AgeP95 = [Math]::Round((Get-Percentile $samples.Age 0.95), 2)
    FullFrames = @($samples | Where-Object Full).Count
    DeltaFrames = @($samples | Where-Object { -not $_.Full }).Count
    PatchAverage = [Math]::Round(($samples | Measure-Object Patches -Average).Average, 2)
    MoveFrames = @($samples | Where-Object Moves -gt 0).Count
    AtlasPixelsP50 = [Math]::Round((Get-Percentile $samples.AtlasPixels 0.5), 0)
    AtlasPixelsP95 = [Math]::Round((Get-Percentile $samples.AtlasPixels 0.95), 0)
    Backend = ($samples | Group-Object Backend | Sort-Object Count -Descending |
        Select-Object -First 1).Name
}
