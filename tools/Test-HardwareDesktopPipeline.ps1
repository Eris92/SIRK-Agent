#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FfmpegPath,
    [ValidateRange(5, 60)][int]$DurationSeconds = 10,
    [ValidateRange(300, 8000)][int]$TargetKbps = 1000,
    [switch]$GenerateWindowMotion
)

$ErrorActionPreference = 'Stop'
$output = Join-Path ([IO.Path]::GetTempPath()) 'sirk-hardware-desktop.h264'
Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
$motion = $null
if ($GenerateWindowMotion) {
    $motion = Start-Job -ArgumentList ($DurationSeconds + 1) -ScriptBlock {
        param($Seconds)
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SirkHardwareBenchmarkWindow {
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
            for ($index = 0; $index -lt ($Seconds * 63); $index++) {
                $x = 80 + [int](420 * (0.5 + 0.5 * [Math]::Sin($index / 18.0)))
                $y = 80 + [int](220 * (0.5 + 0.5 * [Math]::Cos($index / 23.0)))
                [SirkHardwareBenchmarkWindow]::MoveWindow(
                    $process.MainWindowHandle, $x, $y, 900, 600, $true) | Out-Null
                Start-Sleep -Milliseconds 16
            }
        } finally { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
}

try {
    $arguments = @(
        '-hide_banner', '-benchmark',
        '-filter_complex', 'ddagrab=output_idx=0:framerate=60:draw_mouse=0:dup_frames=1',
        '-t', [string]$DurationSeconds,
        '-c:v', 'h264_mf', '-hw_encoding', '1', '-scenario', 'display_remoting',
        '-rate_control', 'cbr', '-b:v', "${TargetKbps}k", '-maxrate', "${TargetKbps}k",
        '-bufsize', "$([math]::Max(100, [int]($TargetKbps / 4)))k",
        '-g', '60', '-bf', '0', '-y', '-f', 'h264', $output
    )
    $lines = & $FfmpegPath @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($lines -join [Environment]::NewLine) }
    $summary = $lines | Select-String 'frame=.*Lsize|bench: utime' | ForEach-Object Line
    $file = Get-Item -LiteralPath $output
    [pscustomobject]@{
        Bytes = $file.Length
        MegabitsPerSecond = [math]::Round($file.Length * 8 / $DurationSeconds / 1000000, 3)
        EncoderSummary = $summary -join ' | '
    }
} finally {
    if ($motion) {
        Wait-Job $motion | Out-Null
        Remove-Job $motion -Force
    }
}
