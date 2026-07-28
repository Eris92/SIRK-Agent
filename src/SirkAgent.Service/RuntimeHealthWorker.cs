using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed class RuntimeHealthWorker : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatWarningAge = TimeSpan.FromSeconds(90);
    private const long EventLogRotateBytes = 10L * 1024 * 1024;
    private const int EventLogArchives = 5;

    private readonly ILogger<RuntimeHealthWorker> _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RuntimeHealthWorker(ILogger<RuntimeHealthWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
        Directory.CreateDirectory(root);
        var heartbeatPath = Path.Combine(root, "heartbeat-latest.json");
        var runtimePath = Path.Combine(root, "runtime-health.json");
        var eventLogPath = Path.Combine(root, "agent-events.jsonl");
        var process = Process.GetCurrentProcess();
        var previousCpu = process.TotalProcessorTime;
        var previousSample = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            process.Refresh();
            var elapsed = now - previousSample;
            var cpuDelta = process.TotalProcessorTime - previousCpu;
            var cpuPercent = elapsed.TotalMilliseconds <= 0
                ? 0
                : Math.Max(0, cpuDelta.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100d);

            DateTimeOffset? heartbeatUtc = null;
            string? heartbeatError = null;
            try
            {
                if (File.Exists(heartbeatPath))
                {
                    using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(heartbeatPath, stoppingToken));
                    if (document.RootElement.TryGetProperty("timestampUtc", out var value) &&
                        DateTimeOffset.TryParse(value.GetString(), out var parsed))
                        heartbeatUtc = parsed;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                heartbeatError = ex.Message;
            }

            var heartbeatAge = heartbeatUtc.HasValue ? now - heartbeatUtc.Value : (TimeSpan?)null;
            var heartbeatFresh = heartbeatAge.HasValue && heartbeatAge.Value <= HeartbeatWarningAge;
            var status = heartbeatFresh ? "Healthy" : "Warning";
            var code = heartbeatFresh ? "RUNTIME_HEALTHY" : heartbeatUtc.HasValue ? "HEARTBEAT_STALE" : "HEARTBEAT_MISSING";

            RotateEventLog(eventLogPath);

            AtomicFile.WriteJson(runtimePath, new
            {
                timestampUtc = now,
                status,
                code,
                processId = Environment.ProcessId,
                processStartUtc = process.StartTime.ToUniversalTime(),
                uptimeSeconds = Math.Max(0, (now - process.StartTime.ToUniversalTime()).TotalSeconds),
                cpuPercent = Math.Round(cpuPercent, 2),
                workingSetBytes = process.WorkingSet64,
                privateMemoryBytes = process.PrivateMemorySize64,
                managedMemoryBytes = GC.GetTotalMemory(false),
                threadCount = process.Threads.Count,
                handleCount = process.HandleCount,
                heartbeatUtc,
                heartbeatAgeSeconds = heartbeatAge?.TotalSeconds,
                heartbeatFresh,
                heartbeatError,
                eventLogBytes = File.Exists(eventLogPath) ? new FileInfo(eventLogPath).Length : 0,
                eventLogRotateBytes = EventLogRotateBytes,
                eventLogArchives = EventLogArchives
            }, _json);

            previousCpu = process.TotalProcessorTime;
            previousSample = now;

            try
            {
                await Task.Delay(SampleInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void RotateEventLog(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < EventLogRotateBytes)
                return;

            for (var index = EventLogArchives - 1; index >= 1; index--)
            {
                var source = $"{path}.{index}";
                var target = $"{path}.{index + 1}";
                if (File.Exists(source)) File.Move(source, target, overwrite: true);
            }

            File.Move(path, $"{path}.1", overwrite: true);
            _logger.LogInformation("Rotated agent event log {Path}.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to rotate agent event log {Path}.", path);
        }
    }
}
