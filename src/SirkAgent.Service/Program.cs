using System.Security.Cryptography;
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
var quarantineProtectedPath = Path.Combine(agentDirectory, "quarantine-state.bin");
var quarantineStatusPath = Path.Combine(agentDirectory, "quarantine-status.json");
var legacyQuarantinePath = Path.Combine(agentDirectory, "quarantine-state.json");
var interval = TimeSpan.FromSeconds(30);
var runOnce = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

Directory.CreateDirectory(agentDirectory);
Console.WriteLine("SIRK Agent Runtime");
Console.WriteLine($"Device:      {deviceId}");
Console.WriteLine($"State:       {statePath}");
Console.WriteLine($"Heartbeat:   {heartbeatPath}");
Console.WriteLine($"Quarantine:  {quarantineProtectedPath}");
Console.WriteLine(runOnce ? "Mode:        once" : $"Mode:        loop ({interval.TotalSeconds:0}s + watcher)");

var protector = new DpapiMachineStateProtector();
var store = new FilePolicyStateStore(statePath, protector);
var checker = new PolicyStateHealthChecker(statePath, store);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var compactJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var quarantine = LoadQuarantineState();

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
        quarantine = quarantine.Active
            ? quarantine with
            {
                LastUpdatedUtc = timestamp,
                LastReason = health.Code,
                LastTrigger = trigger,
                DetectionCount = quarantine.DetectionCount + 1
            }
            : new QuarantineState(
                Active: true,
                SinceUtc: timestamp,
                Reason: health.Code,
                Trigger: trigger,
                LastUpdatedUtc: timestamp,
                LastReason: health.Code,
                LastTrigger: trigger,
                DetectionCount: 1);

        SaveQuarantineState(quarantine);
        WriteAtomically(tamperEventPath, JsonSerializer.SerializeToUtf8Bytes(new TamperEvent(
            timestamp,
            tenantId,
            deviceId,
            trigger,
            health.Code,
            health.Message,
            statePath,
            quarantine.SinceUtc,
            quarantine.DetectionCount), jsonOptions));
    }
    else if (quarantine.Active)
    {
        quarantine = quarantine with
        {
            LastUpdatedUtc = timestamp,
            LastReason = "POLICY_STATE_HEALTHY_QUARANTINE_RETAINED",
            LastTrigger = trigger
        };
        SaveQuarantineState(quarantine);
    }

    WriteAtomically(quarantineStatusPath, JsonSerializer.SerializeToUtf8Bytes(quarantine, jsonOptions));

    var heartbeat = PolicyHeartbeatFactory.Create(
        state,
        tenantId,
        deviceId,
        timestamp,
        health.Code,
        trigger,
        quarantine.Active,
        quarantine.Active ? quarantine.SinceUtc : null,
        quarantine.Active ? quarantine.Reason : null);

    WriteAtomically(heartbeatPath, JsonSerializer.SerializeToUtf8Bytes(heartbeat, jsonOptions));
    AppendEvent(eventLogPath, new AgentEvent(
        timestamp,
        trigger,
        health.Code,
        health.Message,
        !health.IsHealthy,
        quarantine.Active,
        quarantine.Reason,
        quarantine.DetectionCount,
        state.ActivePolicyId,
        state.ActivePolicyHash));

    Console.WriteLine($"{timestamp:O} trigger={trigger} heartbeat={health.Code} tamper={!health.IsHealthy} quarantine={quarantine.Active} detections={quarantine.DetectionCount} policy={state.ActivePolicyId ?? "none"} version={state.Version}");

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

QuarantineState LoadQuarantineState()
{
    try
    {
        if (File.Exists(quarantineProtectedPath))
        {
            var encrypted = File.ReadAllBytes(quarantineProtectedPath);
            if (encrypted.Length == 0)
                throw new InvalidDataException("Protected quarantine state is empty.");

            var plaintext = protector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<QuarantineState>(plaintext, compactJsonOptions)
                   ?? throw new InvalidDataException("Protected quarantine state could not be deserialized.");
        }

        if (File.Exists(legacyQuarantinePath))
        {
            var migrated = JsonSerializer.Deserialize<QuarantineState>(File.ReadAllBytes(legacyQuarantinePath), compactJsonOptions)
                           ?? throw new InvalidDataException("Legacy quarantine state could not be deserialized.");
            SaveQuarantineState(migrated);
            File.Move(legacyQuarantinePath, legacyQuarantinePath + ".migrated", overwrite: true);
            return migrated;
        }

        return QuarantineState.Inactive;
    }
    catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
    {
        PreserveCorruptedQuarantineFile();
        var timestamp = DateTimeOffset.UtcNow;
        var recovered = new QuarantineState(
            Active: true,
            SinceUtc: timestamp,
            Reason: "QUARANTINE_STATE_TAMPER",
            Trigger: "Startup",
            LastUpdatedUtc: timestamp,
            LastReason: exception.GetType().Name,
            LastTrigger: "Startup",
            DetectionCount: 1);
        SaveQuarantineState(recovered);
        return recovered;
    }
}

void SaveQuarantineState(QuarantineState value)
{
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, compactJsonOptions);
    var encrypted = protector.Protect(plaintext);
    WriteAtomically(quarantineProtectedPath, encrypted);
}

void PreserveCorruptedQuarantineFile()
{
    if (!File.Exists(quarantineProtectedPath))
        return;

    var evidencePath = Path.Combine(
        agentDirectory,
        $"quarantine-state.tampered.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bin");
    File.Copy(quarantineProtectedPath, evidencePath, overwrite: false);
}

static void WriteAtomically(string path, byte[] content)
{
    var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
    try
    {
        using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tempPath, path, overwrite: true);
    }
    finally
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
}

static void AppendEvent(string path, AgentEvent agentEvent)
{
    var line = JsonSerializer.Serialize(agentEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    File.AppendAllText(path, line + Environment.NewLine);
}

internal sealed record QuarantineState(
    bool Active,
    DateTimeOffset? SinceUtc,
    string? Reason,
    string? Trigger,
    DateTimeOffset? LastUpdatedUtc,
    string? LastReason,
    string? LastTrigger,
    long DetectionCount)
{
    public static QuarantineState Inactive { get; } = new(false, null, null, null, null, null, null, 0);
}

internal sealed record TamperEvent(
    DateTimeOffset TimestampUtc,
    string TenantId,
    string DeviceId,
    string Trigger,
    string Code,
    string Message,
    string StatePath,
    DateTimeOffset? QuarantineSinceUtc,
    long DetectionCount);

internal sealed record AgentEvent(
    DateTimeOffset TimestampUtc,
    string Trigger,
    string Code,
    string Message,
    bool TamperDetected,
    bool QuarantineActive,
    string? QuarantineReason,
    long DetectionCount,
    string? ActivePolicyId,
    string? ActivePolicyHash);