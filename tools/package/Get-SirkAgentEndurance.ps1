[CmdletBinding()]
param(
    [switch]$OpenHtml
)

$Root = Join-Path $env:ProgramData 'SIRK\Agent'
$SummaryPath = Join-Path $Root 'endurance-summary.json'
$HtmlPath = Join-Path $Root 'endurance-report.html'

if (-not (Test-Path $SummaryPath)) {
    throw "Brak raportu endurance: $SummaryPath. Odczekaj co najmniej jeden cykl agenta."
}

$Summary = Get-Content $SummaryPath -Raw | ConvertFrom-Json

[pscustomobject]@{
    Status               = $Summary.status
    Samples              = $Summary.sampleCount
    DurationHours        = [math]::Round([double]$Summary.durationHours, 2)
    ProcessRestarts      = $Summary.processRestarts
    SampleGaps           = $Summary.sampleGaps
    UnhealthySamples     = $Summary.unhealthySamples
    CpuMin               = $Summary.cpuMin
    CpuAverage           = [math]::Round([double]$Summary.cpuAverage, 2)
    CpuMax               = $Summary.cpuMax
    RamMinMB             = [math]::Round([double]$Summary.workingSetMin / 1MB, 2)
    RamAverageMB         = [math]::Round([double]$Summary.workingSetAverage / 1MB, 2)
    RamMaxMB             = [math]::Round([double]$Summary.workingSetMax / 1MB, 2)
    RamGrowthPerHourMB   = [math]::Round([double]$Summary.workingSetGrowthPerHour / 1MB, 2)
    MemoryLeakSuspected  = $Summary.memoryLeakSuspected
    TelemetryFiles       = $Summary.telemetryFiles
    TelemetryMB          = [math]::Round([double]$Summary.telemetryBytes / 1MB, 2)
    EvidenceMB           = [math]::Round([double]$Summary.evidenceBytes / 1MB, 2)
    DeviceId             = $Summary.deviceId
    JsonReport           = $SummaryPath
    HtmlReport           = $HtmlPath
} | Format-List

if ($OpenHtml) {
    if (-not (Test-Path $HtmlPath)) { throw "Brak raportu HTML: $HtmlPath" }
    Start-Process $HtmlPath
}
