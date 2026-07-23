namespace WorkspaceCommon;

public sealed record WorkspaceStatus(
    string Type,
    string SessionToken,
    int Pid,
    int SessionId,
    string User,
    string Desktop,
    string Version,
    DateTimeOffset Timestamp,
    long UptimeSeconds);
