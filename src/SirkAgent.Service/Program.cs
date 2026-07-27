using System.Text.Json;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

const string tenantId = "investa";
var startedAtUtc = DateTimeOffset.UtcNow;
var paths = AgentPaths.CreateDefault();
var interval = TimeSpan.FromSeconds(30);
var runOnce = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

paths.EnsureDirectories();

var protector = new DpapiMachineStateProtector();
var identityStore = new DeviceIdentityStore(paths.DeviceIdentityPath, protector);
var identity = identityStore.LoadOrCreate(tenantId);
var deviceId = identity.DeviceId;
var policyStore = new FilePolicyStateStore(paths.PolicyStatePath, protector);
var policyChecker = new PolicyStateHealthChecker(paths.PolicyStatePath, policyStore);
var quarantineStore = new QuarantineStore(paths, protector);
var quarantineLoad = quarantineStore.Load();
var quarantine = quarantineLoad.State;
var stateMachine = new SecurityStateMachine(startedAtUtc);
var healthRegistry = new ModuleHealthRegistry();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

healthRegistry.Report(new ModuleHealthSnapshot(
    "Device Identity",
    ModuleHealthStatus.Healthy,
    "DEVICE_IDENTITY_OK",
    "Persistent DPAPI protected device identity loaded.",
    startedAtUtc,
    startedAtUtc,
    null,
    new Dictionary<string, string?>
    {
        ["deviceId"] = deviceId,
        ["tenantId"] = tenantId,
        ["path"] = paths.DeviceIdentityPath,
        ["machineName"] = Environment.MachineName
    }));

healthRegistry.Report(new ModuleHealthSnapshot(
    "Quarantine Store",
    quarantineLoad.TamperDetected ? ModuleHealthStatus.Critical : ModuleHealthStatus.Healthy,
    quarantineLoad.TamperDetected ? "QUARANTINE_STATE_TAMPER" : "QUARANTINE_STORE_OK",
    quarantineLoad.TamperDetected ? "Quarantine state integrity failure detected." : "Protected quarantine state loaded.",
    startedAtUtc,
    quarantineLoad.TamperDetected ? null : startedAtUtc,
    quarantineLoad.Error,
    new Dictionary<string, string?>
    {
        ["path"] = paths.QuarantineProtectedPath,
        ["active"] = quarantine.Active.ToString(),
        ["loadNote"] = quarantineLoad.Error is null ? "none" : "see error"
    }));

Console.WriteLine("SIRK Agent Runtime");
Console.WriteLine($"Machine:     {Environment.MachineName}");
Console.WriteLine($"Device ID:   {deviceId}");
Console.WriteLine($"Identity:    {paths.DeviceIdentityPath}");
Console.WriteLine($"State:       {paths.PolicyStatePath}");
Console.WriteLine($"Heartbeat:   {paths.HeartbeatPath}");
Console.WriteLine($"Security:    {paths.SecurityStatePath}");
Console.WriteLine($"Quarantine:  {paths.QuarantineProtectedPath}");
Console.WriteLine(runOnce ? "Mode:        once" : $"Mode:        loop ({interval.TotalSeconds:0}s + watcher)");

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

healthRegistry.Report(new ModuleHealthSnapshot(
    "Tamper Watcher",
    runOnce ? ModuleHealthStatus.Warning : ModuleHealthStatus.Healthy,
    runOnce ? "WATCHER_DISABLED_ONCE_MODE" : "WATCHER_ACTIVE",
    runOnce ? "File watcher is disabled in one-shot mode." : "Policy state watcher is active.",
    startedAtUtc,
    runOnce ? null : startedAtUtc,
    null,
    new Dictionary<string, string?>
    {
        ["path"] = paths.PolicyStatePath,
        ["debounceMs"] = "350"
    }));

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
    var policyHealth = policyChecker.Check();
    var policyState = policyHealth.State ?? PolicyState.Empty;

    healthRegistry.Report(new ModuleHealthSnapshot(
        "Policy State",
        policyHealth.IsHealthy ? ModuleHealthStatus.Healthy : ModuleHealthStatus.Critical,
        policyHealth.Code,
        policyHealth.Message,
        timestamp,
        policyHealth.IsHealthy ? timestamp : null,
        policyHealth.IsHealthy ? null : policyHealth.Message,
        new Dictionary<string, string?>
        {
            ["path"] = paths.PolicyStatePath,
            ["policyId"] = policyState.ActivePolicyId,
            ["version"] = policyState.Version.ToString(),
            ["hash"] = policyState.ActivePolicyHash,
            ["trigger"] = trigger
        }));

    if (!policyHealth.IsHealthy)
    {
        quarantine = quarantine.Active
            ? quarantine with
            {
                LastUpdatedUtc = timestamp,
                LastReason = policyHealth.Code,
                LastTrigger = trigger,
                DetectionCount = quarantine.DetectionCount + 1
            }
            : new QuarantineState(
                true,
                timestamp,
                policyHealth.Code,
                trigger,
                timestamp,
                policyHealth.Code,
                trigger,
                1);

        quarantineStore.Save(quarantine);
        AtomicFile.WriteJson(paths.TamperEventPath, new TamperEvent(
            timestamp,
            tenantId,
            deviceId,
            trigger,
            policyHealth.Code,
            policyHealth.Message,
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
        quarantineStore.Save(quarantine);
    }

    AtomicFile.WriteJson(paths.QuarantineStatusPath, quarantine, jsonOptions);

    healthRegistry.Report(new ModuleHealthSnapshot(
        "Quarantine",
        quarantine.Active ? ModuleHealthStatus.Critical : ModuleHealthStatus.Healthy,
        quarantine.Active ? quarantine.Reason ?? "QUARANTINE_ACTIVE" : "QUARANTINE_INACTIVE",
        quarantine.Active ? "Device remains in persistent quarantine." : "Quarantine is inactive.",
        timestamp,
        quarantine.Active ? null : timestamp,
        null,
        new Dictionary<string, string?>
        {
            ["active"] = quarantine.Active.ToString(),
            ["sinceUtc"] = quarantine.SinceUtc?.ToString("O"),
            ["reason"] = quarantine.Reason,
            ["detectionCount"] = quarantine.DetectionCount.ToString(),
            ["protectedPath"] = paths.QuarantineProtectedPath
        }));

    var securityState = stateMachine.Evaluate(timestamp, policyHealth.IsHealthy, policyHealth.Code, quarantine.Active);
    AtomicFile.WriteJson(paths.SecurityStatePath, new SecurityRuntimeSnapshot(
        securityState,
        healthRegistry.OverallStatus().ToString(),
        healthRegistry.Snapshot()), jsonOptions);

    healthRegistry.Report(new ModuleHealthSnapshot(
        "Security State Machine",
        securityState.State is "Operational" ? ModuleHealthStatus.Healthy : securityState.State is "Degraded" or "PolicyExpired" ? ModuleHealthStatus.Warning : ModuleHealthStatus.Critical,
        securityState.Reason,
        $"Current security state: {securityState.State}.",
        timestamp,
        securityState.State is "Operational" ? timestamp : null,
        null,
        new Dictionary<string, string?>
        {
            ["state"] = securityState.State,
            ["changedAtUtc"] = securityState.StateChangedAtUtc.ToString("O"),
            ["uptimeSeconds"] = securityState.UptimeSeconds.ToString(),
            ["path"] = paths.SecurityStatePath
        }));

    var heartbeat = PolicyHeartbeatFactory.Create(
        policyState,
        tenantId,
        deviceId,
        timestamp,
        policyHealth.Code,
        trigger,
        quarantine.Active,
        quarantine.Active ? quarantine.SinceUtc : null,
        quarantine.Active ? quarantine.Reason : null);

    AtomicFile.WriteJson(paths.HeartbeatPath, heartbeat, jsonOptions);
    AtomicFile.AppendJsonLine(paths.EventLogPath, new AgentEvent(
        timestamp,
        trigger,
        policyHealth.Code,
        policyHealth.Message,
        !policyHealth.IsHealthy,
        quarantine.Active,
        quarantine.Reason,
        quarantine.DetectionCount,
        policyState.ActivePolicyId,
        policyState.ActivePolicyHash,
        securityState.State,
        healthRegistry.OverallStatus().ToString()));

    Console.WriteLine($"{timestamp:O} trigger={trigger} security={securityState.State} health={healthRegistry.OverallStatus()} policy={policyHealth.Code} quarantine={quarantine.Active} detections={quarantine.DetectionCount}");

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

var stoppingAtUtc = DateTimeOffset.UtcNow;
var stoppingState = stateMachine.Stop(stoppingAtUtc);
AtomicFile.WriteJson(paths.SecurityStatePath, new SecurityRuntimeSnapshot(
    stoppingState,
    healthRegistry.OverallStatus().ToString(),
    healthRegistry.Snapshot()), jsonOptions);

return 0;

internal sealed record SecurityRuntimeSnapshot(
    SecurityStateSnapshot Security,
    string OverallHealth,
    IReadOnlyList<ModuleHealthSnapshot> Modules);

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
    string? ActivePolicyHash,
    string SecurityState,
    string OverallHealth);
