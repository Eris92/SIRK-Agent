using System.Text.Json;
using SirkAgent.Policy;

const string tenantId = "investa";
var deviceId = Environment.MachineName;
var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
var agentDirectory = Path.Combine(programData, "SIRK", "Agent");
var statePath = Path.Combine(agentDirectory, "policy-state.bin");
var heartbeatPath = Path.Combine(agentDirectory, "heartbeat-latest.json");
var eventLogPath = Path.Combine(agentDirectory, "agent-events.jsonl");
var interval = TimeSpan.FromSeconds(30);
var runOnce = args.Any(a => string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase));

Directory.CreateDirectory(agentDirectory);
Console.WriteLine("SIRK Agent Runtime");
Console.WriteLine($"Device:    {deviceId}");
Console.WriteLine($"State:     {statePath}");
Console.WriteLine($"Heartbeat: {heartbeatPath}");
Console.WriteLine(runOnce ? "Mode:      once" : $"Mode:      loop ({interval.TotalSeconds:0}s)");

var store = new FilePolicyStateStore(statePath, new DpapiMachineStateProtector());
var monitor = new PolicyStateHealthMonitor(store, statePath);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

while (!cancellation.IsCancellationRequested)
{
    var timestamp = DateTimeOffset.UtcNow;
    var health = monitor.Check();
    PolicyState state;

    try
    {
        state = health.IsHealthy ? store.Load() : PolicyState.Empty;
    }
    catch (Exception exception)
    {
        health = new PolicyStateHealthResult(false, "STATE_LOAD_FAILED", exception.Message);
        state = PolicyState.Empty;
    }

    var heartbeat = PolicyHeartbeatFactory.Create(
        state,
        tenantId,
        deviceId,
        timestamp,
        health.Code);

    WriteAtomically(heartbeatPath, JsonSerializer.SerializeToUtf8Bytes(heartbeat, jsonOptions));
    AppendEvent(eventLogPath, new AgentEvent(timestamp, health.Code, health.Message, state.ActivePolicyId, state.ActivePolicyHash));

    Console.WriteLine($"{timestamp:O} heartbeat={health.Code} policy={state.ActivePolicyId ?? "none"} version={state.Version}");

    if (runOnce)
        break;

    try
    {
        await Task.Delay(interval, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

return 0;

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

internal sealed record AgentEvent(
    DateTimeOffset TimestampUtc,
    string Code,
    string Message,
    string? ActivePolicyId,
    string? ActivePolicyHash);