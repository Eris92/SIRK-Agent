#requires -Version 7.0
[CmdletBinding()]
param([ValidateRange(2, 120)][int]$DurationSeconds = 15)

$source = @'
using System;
using System.Runtime.InteropServices;
public static class SirkMotionWindow {
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
}
'@
Add-Type $source
$process = Start-Process mspaint -PassThru
try {
    for ($attempt = 0; $attempt -lt 50 -and $process.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) { throw 'Nie udało się utworzyć widocznego okna testowego.' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $frame = 0
    $nextFrameMilliseconds = 0.0
    while ($clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $x = 80 + [int](420 * (0.5 + 0.5 * [Math]::Sin($frame / 18.0)))
        $y = 80 + [int](220 * (0.5 + 0.5 * [Math]::Cos($frame / 23.0)))
        [SirkMotionWindow]::MoveWindow($process.MainWindowHandle, $x, $y, 900, 600, $true) | Out-Null
        $frame++
        $nextFrameMilliseconds += 1000.0 / 60.0
        while (($remaining = $nextFrameMilliseconds - $clock.Elapsed.TotalMilliseconds) -gt 0) {
            if ($remaining -gt 2) { Start-Sleep -Milliseconds 1 } else { [Threading.Thread]::SpinWait(250) }
        }
    }
    [pscustomobject]@{ Frames = $frame; FPS = $frame / $clock.Elapsed.TotalSeconds }
} finally {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
