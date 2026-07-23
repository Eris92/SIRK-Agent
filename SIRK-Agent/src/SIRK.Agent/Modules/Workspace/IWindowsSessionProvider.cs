namespace Sirk.Agent.Modules.Workspace;

internal interface IWindowsSessionProvider
{
    uint? ActiveConsoleSessionId { get; }

    bool IsInteractiveSessionAvailable(uint sessionId);
}
