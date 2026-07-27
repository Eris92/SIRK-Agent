using System.Security.Cryptography;
using System.Text.Json;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

const string tenantId = "investa";
var paths = AgentPaths.CreateDefault();
var interval = TimeSpan.FromSeconds(30);
var runOnce = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

paths.EnsureDirectories();

var protector = new DpapiMachineStateProtector();
var identityStore = new DeviceIdentityStore(paths.DeviceIdentityPath, protector);
var identity = identityStore.LoadOrCreate(tenantId);
var deviceId = identity.DeviceId;

Console.WriteLine("SIRK Agent Runtime");
Console.WriteLine($"Machine:     {Environment.MachineName}");
Console.WriteLine($"Device ID:   {deviceId}");
Console.WriteLine($"Identity:    {paths.DeviceIdentityPath}");
Console.WriteLine($"State:       {paths.PolicyStatePath}");
Console.WriteLine($"Heartbeat:   {paths.HeartbeatPath}");
Console.WriteLine($"Quarantine:  {paths.QuarantineProtectedPath}");
Console.WriteLine(runOnce ? "Mode:        once" : $"Mode:        loop ({interval.TotalSeconds:0}s + watcher)");

var store = new FilePolicyStateStore(paths.PolicyStatePath, protector);
var checker = new PolicyStateHealthChecker(paths.PolicyStatePath, store);
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
using var watcher = new FileSystemWatcher(paths.AgentDirectory, Path.GetFileName(paths.PolicyStatePath))
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
        AtomicFile.WriteJson(paths.TamperEventPath, new TamperEvent(
            timestamp,
            tenantId,
            deviceId,
            trigger,
            health.Code,
            health.Message,
            paths.PolicyStatePath,
            quarantine.SinceUtc,
            quarantine.DetectionCount), jsonOptions);
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

    AtomicFile.WriteJson(paths.QuarantineStatusPath, quarantine, jsonOptions);

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

    AtomicFile.WriteJson(paths.HeartbeatPath, heartbeat, jsonOptions);
    AtomicFile.AppendJsonLine(paths.EventLogPath, new AgentEvent(
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
        if (File.Exists(paths.QuarantineProtectedPath))
        {
            var encrypted = File.ReadAllBytes(paths.QuarantineProtectedPath);
            if (encrypted.Length == 0)
                throw new InvalidDataException("Protected quarantine state is empty.");

            var plaintext = protector.Unprotect(encrypted);
            return JsonSerializer.Deserialize<QuarantineState>(plaintext, compactJsonOptions)
                   ?? throw new InvalidDataException("Protected quarantine state could not be deserialized.");
        }

        if (File.Exists(paths.LegacyQuarantinePath))
        {
            var migrated = JsonSerializer.Deserialize<QuarantineState>(File.ReadAllBytes(paths.LegacyQuarantinePath), compactJsonOptions)
                           ?? throw new InvalidDataException("Legacy quarantine state could not be deserialized.");
            SaveQuarantineState(migrated);
            File.Move(paths.LegacyQuarantinePath, paths.LegacyQuarantinePath + ".migrated", overwrite: true);
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
    AtomicFile.Write(paths.QuarantineProtectedPath, encrypted);
}

void PreserveCorruptedQuarantineFile()
{
    if (!File.Exists(paths.QuarantineProtectedPath))
        return;

    var evidencePath = Path.Combine(
        paths.AgentDirectory,
        $"quarantine-state.tampered.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bin");
    File.Copy(paths.QuarantineProtectedPath, evidencePath, overwrite: false);
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
