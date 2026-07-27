namespace SirkAgent.Service.Core;

internal sealed record AgentPaths(
    string AgentDirectory,
    string PolicyStatePath,
    string HeartbeatPath,
    string EventLogPath,
    string TamperEventPath,
    string QuarantineProtectedPath,
    string QuarantineStatusPath,
    string LegacyQuarantinePath,
    string DeviceIdentityPath)
{
    public static AgentPaths CreateDefault()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var agentDirectory = Path.Combine(programData, "SIRK", "Agent");

        return new AgentPaths(
            agentDirectory,
            Path.Combine(agentDirectory, "policy-state.bin"),
            Path.Combine(agentDirectory, "heartbeat-latest.json"),
            Path.Combine(agentDirectory, "agent-events.jsonl"),
            Path.Combine(agentDirectory, "tamper-event-latest.json"),
            Path.Combine(agentDirectory, "quarantine-state.bin"),
            Path.Combine(agentDirectory, "quarantine-status.json"),
            Path.Combine(agentDirectory, "quarantine-state.json"),
            Path.Combine(agentDirectory, "device-identity.bin"));
    }

    public void EnsureDirectories() => Directory.CreateDirectory(AgentDirectory);
}
