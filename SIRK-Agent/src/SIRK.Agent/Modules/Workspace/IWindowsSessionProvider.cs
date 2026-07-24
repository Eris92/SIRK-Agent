namespace Sirk.Agent.Modules.Workspace;

internal interface IWindowsSessionProvider
{
    uint? ActiveConsoleSessionId { get; }

    IReadOnlyList<WindowsSessionInfo> GetSessions();

    bool IsInteractiveSessionAvailable(uint sessionId);
}

internal sealed record WindowsSessionInfo(
    uint SessionId,
    string StationName,
    string State,
    bool IsInteractive);