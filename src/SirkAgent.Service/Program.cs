using System.Text.Json;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

const string tenantId = "investa";
var startedAtUtc = DateTimeOffset.UtcNow;
var paths = AgentPaths.CreateDefault();
var interval = TimeSpan.FromSeconds(30);
var debounce = TimeSpan.FromMilliseconds(350);
var runOnce = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

paths.EnsureDirectories();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
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
var healthMonitor = new HealthMonitor(paths.SecurityStatePath, healthRegistry, jsonOptions);
var scheduler = new AgentScheduler(
    paths.AgentDirectory,
    Path.GetFileName(paths.PolicyStatePath),
    interval,
    debounce,
    runOnce);

ReportHealth(
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
    });

ReportHealth(
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
        ["active"] = quarantine.Active.ToString()
    });

ReportHealth(
    "Scheduler",
    ModuleHealthStatus.Healthy,
    runOnce ? "SCHEDULER_ONCE_MODE" : "SCHEDULER_ACTIVE",
    runOnce ? "Scheduler will execute one startup cycle." : "Scheduler is active with interval and file watcher triggers.",
    startedAtUtc,
    startedAtUtc,
    null,
    new Dictionary<string, string?>
    {
        ["intervalSeconds"] = interval.TotalSeconds.ToString("0"),
        ["debounceMilliseconds"] = debounce.TotalMilliseconds.ToString("0"),
        ["watchPath"] = paths.PolicyStatePath
    });

ReportHealth(
    "Tamper Watcher",
    runOnce ? ModuleHealthStatus.Warning : ModuleHealthStatus.Healthy,
    runOnce ? "WATCHER_DISABLED_ONCE_MODE" : "WATCHER_ACTIVE",
    runOnce ? "File watcher is disabled in one-shot mode." : "Policy state watcher is managed by the scheduler.",
    startedAtUtc,
    runOnce ? null : startedAtUtc,
    null,
    new Dictionary<string, string?>
    {
        ["path"] = paths.PolicyStatePath,
        ["debounceMs"] = debounce.TotalMilliseconds.ToString("0")
    });

Console.WriteLine("SIRK Agent Runtime");
Console.WriteLine($"Machine:     {Environment.MachineName}");
Console.WriteLine($"Device ID:   {deviceId}");
Console.WriteLine($"Identity:    {paths.DeviceIdentityPath}");
Console.WriteLine($"State:       {paths.PolicyStatePath}");
Console.WriteLine($"Heartbeat:   {paths.HeartbeatPath}");
Console.WriteLine($"Security:    {paths.SecurityStatePath}");
Console.WriteLine($"Quarantine:  {paths.QuarantineProtectedPath}");
Console.WriteLine(runOnce ? "Mode:        once" : $"Mode:        scheduled ({interval.TotalSeconds:0}s + watcher)");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

await foreach (var scheduledTrigger in scheduler.RunAsync(cancellation.Token))
{
    var trigger = scheduledTrigger.Name;
    var timestamp = scheduledTrigger.TimestampUtc;
    var policyHealth = policyChecker.Check();
    var policyState = policyHealth.State ?? PolicyState.Empty;

    ReportHealth(
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
            ["trigger"] = trigger,
            ["triggerDetail"] = scheduledTrigger.Detail
        });

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

    ReportHealth(
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
        });

    var securityState = stateMachine.Evaluate(timestamp, policyHealth.IsHealthy, policyHealth.Code, quarantine.Active);

    ReportHealth(
        "Security State Machine",
        securityState.State is "Operational"
            ? ModuleHealthStatus.Healthy
            : securityState.State is "Degraded" or "PolicyExpired"
                ? ModuleHealthStatus.Warning
                : ModuleHealthStatus.Critical,
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
        });

    healthMonitor.Capture(securityState);

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
        scheduledTrigger.Detail,
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

    Console.WriteLine($"{timestamp:O} trigger={trigger} detail={scheduledTrigger.Detail ?? "none"} security={securityState.State} health={healthRegistry.OverallStatus()} policy={policyHealth.Code} quarantine={quarantine.Active} detections={quarantine.DetectionCount}");
}

var stoppingAtUtc = DateTimeOffset.UtcNow;
var stoppingState = stateMachine.Stop(stoppingAtUtc);
ReportHealth(
    "Scheduler",
    ModuleHealthStatus.Warning,
    "SCHEDULER_STOPPED",
    "Scheduler stopped because the agent is shutting down.",
    stoppingAtUtc,
    null,
    null,
    new Dictionary<string, string?>());
healthMonitor.Capture(stoppingState);

return 0;

void ReportHealth(
    string module,
    ModuleHealthStatus status,
    string code,
    string summary,
    DateTimeOffset updatedAtUtc,
    DateTimeOffset? lastSuccessAtUtc,
    string? error,
    IReadOnlyDictionary<string, string?> details)
{
    healthRegistry.Report(new ModuleHealthSnapshot(
        module,
        status,
        code,
        summary,
        updatedAtUtc,
        lastSuccessAtUtc,
        error,
        details));
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
    string? TriggerDetail,
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
