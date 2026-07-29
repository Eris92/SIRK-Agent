using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace SirkAgent.Watchdog;

internal sealed class WatchdogWorker : BackgroundService
{
    private const string ProtectedServiceName = "SirkAgent";
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatMaximumAge = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(15);
    private const long PrivateMemoryLimitBytes = 1024L * 1024 * 1024;
    private const double CpuLimitPercent = 90;
    private const int MaximumRestartsPerWindow = 3;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Queue<DateTimeOffset> _restarts = new();
    private TimeSpan? _previousCpu;
    private DateTimeOffset? _previousSample;
    private int _unhealthySamples;
    private DateTimeOffset? _lastIncidentAtUtc;
    private string? _lastIncidentAction;
    private string? _lastIncidentCode;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent", "Watchdog");
        Directory.CreateDirectory(root);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            WatchdogSample sample;
            try { sample = Inspect(now); }
            catch (Exception error)
            {
                sample = new WatchdogSample(now, "InspectionFailed", null, null, null, null,
                    false, "WATCHDOG_INSPECTION_FAILED", error.Message);
            }

            if (sample.RequiresRecovery) _unhealthySamples++;
            else _unhealthySamples = 0;

            var action = "None";
            if (_unhealthySamples >= sample.RequiredSamples)
            {
                action = Recover(now, sample, root);
                _unhealthySamples = 0;
            }
            WriteAtomic(Path.Combine(root, "watchdog-status.json"), new
            {
                sample.TimestampUtc, status = sample.Status, sample.Code, sample.Detail,
                sample.ProcessId, sample.HeartbeatAgeSeconds, sample.CpuPercent,
                sample.PrivateMemoryBytes, unhealthySamples = _unhealthySamples,
                action, recentRestartCount = _restarts.Count,
                lastIncident = _lastIncidentAtUtc is null ? null : new
                {
                    atUtc = _lastIncidentAtUtc,
                    action = _lastIncidentAction,
                    code = _lastIncidentCode
                }
            });
            try { await Task.Delay(SampleInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private WatchdogSample Inspect(DateTimeOffset now)
    {
        using var service = new ServiceController(ProtectedServiceName);
        service.Refresh();
        if (service.Status is not ServiceControllerStatus.Running)
            return new(now, service.Status.ToString(), null, null, null, null,
                true, "SERVICE_NOT_RUNNING", "Główna usługa nie działa.", 1);

        var process = Process.GetProcessesByName("SirkAgent.Service")
            .OrderBy(value => value.SessionId).FirstOrDefault();
        if (process is null)
            return new(now, "ProcessMissing", null, null, null, null,
                true, "PROCESS_MISSING", "SCM raportuje Running, ale nie znaleziono procesu.", 1);
        using (process)
        {
            process.Refresh();
            var cpu = 0d;
            if (_previousCpu.HasValue && _previousSample.HasValue)
            {
                var elapsed = now - _previousSample.Value;
                var delta = process.TotalProcessorTime - _previousCpu.Value;
                if (elapsed.TotalMilliseconds > 0)
                    cpu = Math.Max(0, delta.TotalMilliseconds /
                        (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100d);
            }
            _previousCpu = process.TotalProcessorTime;
            _previousSample = now;
            var heartbeatAge = HeartbeatAge(now);
            if (heartbeatAge is null || heartbeatAge > HeartbeatMaximumAge)
                return new(now, "Unresponsive", process.Id, heartbeatAge?.TotalSeconds, cpu,
                    process.PrivateMemorySize64, true, "HEARTBEAT_STALE",
                    "Heartbeat głównej usługi nie postępuje.", 3);
            if (process.PrivateMemorySize64 > PrivateMemoryLimitBytes)
                return new(now, "ResourceLimit", process.Id, heartbeatAge.Value.TotalSeconds, cpu,
                    process.PrivateMemorySize64, true, "MEMORY_LIMIT_EXCEEDED",
                    "Pamięć prywatna przekroczyła 1 GiB.", 3);
            if (cpu > CpuLimitPercent)
                return new(now, "ResourceLimit", process.Id, heartbeatAge.Value.TotalSeconds, cpu,
                    process.PrivateMemorySize64, true, "CPU_LIMIT_EXCEEDED",
                    "CPU przekroczyło 90%.", 6);
            return new(now, "Healthy", process.Id, heartbeatAge.Value.TotalSeconds, cpu,
                process.PrivateMemorySize64, false, "WATCHDOG_HEALTHY", null);
        }
    }

    private static TimeSpan? HeartbeatAge(DateTimeOffset now)
    {
        var path = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent", "heartbeat-latest.json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.TryGetProperty("timestampUtc", out var value) &&
               DateTimeOffset.TryParse(value.GetString(), out var timestamp)
            ? now - timestamp : null;
    }

    private string Recover(DateTimeOffset now, WatchdogSample sample, string root)
    {
        while (_restarts.Count > 0 && now - _restarts.Peek() > RestartWindow) _restarts.Dequeue();
        if (_restarts.Count >= MaximumRestartsPerWindow)
        {
            AppendIncident(root, now, sample, "RestartSuppressed");
            return "RestartSuppressed";
        }
        try
        {
            using var service = new ServiceController(ProtectedServiceName);
            service.Refresh();
            if (service.Status is not ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            _restarts.Enqueue(now);
            AppendIncident(root, now, sample, "ServiceRestarted");
            return "ServiceRestarted";
        }
        catch (Exception error)
        {
            AppendIncident(root, now, sample, "RecoveryFailed", error.Message);
            return "RecoveryFailed";
        }
    }

    private void AppendIncident(string root, DateTimeOffset now, WatchdogSample sample,
        string action, string? error = null)
    {
        _lastIncidentAtUtc = now;
        _lastIncidentAction = action;
        _lastIncidentCode = sample.Code;
        var value = JsonSerializer.Serialize(new
        {
            incidentId = Guid.NewGuid(), timestampUtc = now, sample.Code, sample.Detail,
            sample.ProcessId, sample.CpuPercent, sample.PrivateMemoryBytes,
            sample.HeartbeatAgeSeconds, action, error
        }, _json);
        File.AppendAllText(Path.Combine(root, "watchdog-incidents.jsonl"), value + Environment.NewLine);
    }

    private void WriteAtomic(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, _json));
        File.Move(temporary, path, true);
    }
}

internal sealed record WatchdogSample(DateTimeOffset TimestampUtc, string Status, int? ProcessId,
    double? HeartbeatAgeSeconds, double? CpuPercent, long? PrivateMemoryBytes, bool RequiresRecovery,
    string Code, string? Detail, int RequiredSamples = 1);
