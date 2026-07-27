using System.Text.Json;
using SirkAgent.Policy;

const string tenantId = "investa";
var deviceId = Environment.MachineName;
var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
var agentDirectory = Path.Combine(programData, "SIRK", "Agent");
var statePath = Path.Combine(agentDirectory, "policy-state.bin");
var heartbeatPath = Path.Combine(agentDirectory, "heartbeat-latest.json");
var eventLogPath = Path.Combine(agentDirectory, "agent-events.jsonl");
var tamperEventPath = Path.Combine(agentDirectory, "tamper-event-latest.json");
var quarantinePath = Path.Combine(agentDirectory, "quarantine-state.json");
var interval = TimeSpan.FromSeconds(30);
var runOnce = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

Directory.CreateDirectory(agentDirectory);
Console.WriteLine("SIRK Agent Runtime");
Console.WriteLine($"Device:     {deviceId}");
Console.WriteLine($"State:      {statePath}");
Console.WriteLine($"Heartbeat:  {heartbeatPath}");
Console.WriteLine($"Quarantine: {quarantinePath}");
Console.WriteLine(runOnce ? "Mode:       once" : $"Mode:       loop ({interval.TotalSeconds:0}s + watcher)");

var store = new FilePolicyStateStore(statePath, new DpapiMachineStateProtector());
var checker = new PolicyStateHealthChecker(statePath, store);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var quarantine = LoadQuarantine(quarantinePath);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using var changeSignal = new SemaphoreSlim(0, 1);
using var watcher = new FileSystemWatcher(agentDirectory, Path.GetFileName(statePath))
{
    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
    IncludeSubdirectories = false,
    EnableRaisingEvents = !runOnce
};

void SignalStateChange(string changeType)
{
    Console.WriteLine($"{DateTimeOffset.UtcNow:O} watcher={changeType}");
    if (changeSignal.CurrentCount == 0)
        changeSignal.Release();
}

watcher.Changed += (_, _) => SignalStateChange("Changed");
watcher.Created += (_, _) => SignalStateChange("Created");
watcher.Deleted += (_, _) => SignalStateChange("Deleted");
watcher.Renamed += (_, _) => SignalStateChange("Renamed");
watcher.Error += (_, eventArgs) => SignalStateChange("WatcherError:" + eventArgs.GetException().GetType().Name);

var trigger = "Startup";
while (!cancellation.IsCancellationRequested)
{
    if (string.Equals(trigger, "FileSystemWatcher", StringComparison.Ordinal))
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    var timestamp = DateTimeOffset.UtcNow;
    var health = checker.Check();
    var state = health.State ?? PolicyState.Empty;

    if (!health.IsHealthy)
    {
        quarantine ??= new QuarantineState(
            Active: true,
            SinceUtc: timestamp,
            Reason: health.Code,
            Trigger: trigger,
            LastUpdatedUtc: timestamp);

        quarantine = quarantine with
        {
            Active = true,
            Reason = health.Code,
            Trigger = trigger,
            LastUpdatedUtc = timestamp
        };

        WriteAtomically(quarantinePath, JsonSerializer.SerializeToUtf8Bytes(quarantine, jsonOptions));
        var tamperEvent = new TamperEvent(timestamp, tenantId, deviceId, trigger, health.Code, health.Message, statePath, quarantine.SinceUtc);
        WriteAtomically(tamperEventPath, JsonSerializer.SerializeToUtf8Bytes(tamperEvent, jsonOptions));
    }

    var heartbeat = PolicyHeartbeatFactory.Create(
        state,
        tenantId,
        deviceId,
        timestamp,
        health.Code,
        trigger,
        quarantineActive: quarantine?.Active == true,
        quarantineSinceUtc: quarantine?.SinceUtc,
        quarantineReason: quarantine?.Reason);

    WriteAtomically(heartbeatPath, JsonSerializer.SerializeToUtf8Bytes(heartbeat, jsonOptions));
    AppendEvent(eventLogPath, new AgentEvent(
        timestamp,
        trigger,
        health.Code,
        health.Message,
        !health.IsHealthy,
        quarantine?.Active == true,
        quarantine?.SinceUtc,
        quarantine?.Reason,
        state.ActivePolicyId,
        state.ActivePolicyHash));

    Console.WriteLine($"{timestamp:O} trigger={trigger} heartbeat={health.Code} tamper={!health.IsHealthy} quarantine={quarantine?.Active == true} policy={state.ActivePolicyId ?? "none"} version={state.Version}");

    if (runOnce)
        break;

    try
    {
        var delayTask = Task.Delay(interval, cancellation.Token);
        var signalTask = changeSignal.WaitAsync(cancellation.Token);
        var completed = await Task.WhenAny(delayTask, signalTask);
        trigger = completed == signalTask ? "FileSystemWatcher" : "Interval";
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

return 0;

static QuarantineState? LoadQuarantine(string path)
{
    try
    {
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<QuarantineState>(File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch
    {
        return new QuarantineState(true, DateTimeOffset.UtcNow, "QUARANTINE_STATE_INVALID", "Startup", DateTimeOffset.UtcNow);
    }
}

static void WriteAtomically(string path, byte[] content)
{
    var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
    File.WriteAllBytes(tempPath, content);
    File.Move(tempPath, path, overwrite: true);
}

static void AppendEvent(string path, AgentEvent agentEvent)
{
    var line = JsonSerializer.Serialize(agentEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    File.AppendAllText(path, line + Environment.NewLine);
}

internal sealed record QuarantineState(
    bool Active,
    DateTimeOffset SinceUtc,
    string Reason,
    string Trigger,
    DateTimeOffset LastUpdatedUtc);

internal sealed record TamperEvent(
    DateTimeOffset TimestampUtc,
    string TenantId,
    string DeviceId,
    string Trigger,
    string Code,
    string Message,
    string StatePath,
    DateTimeOffset QuarantineSinceUtc);

internal sealed record AgentEvent(
    DateTimeOffset TimestampUtc,
    string Trigger,
    string Code,
    string Message,
    bool TamperDetected,
    bool QuarantineActive,
    DateTimeOffset? QuarantineSinceUtc,
    string? QuarantineReason,
    string? ActivePolicyId,
    string? ActivePolicyHash);
