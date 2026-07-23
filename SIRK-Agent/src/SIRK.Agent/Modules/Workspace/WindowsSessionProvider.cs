using System.Runtime.InteropServices;

namespace Sirk.Agent.Modules.Workspace;

internal sealed class WindowsSessionProvider : IWindowsSessionProvider
{
    private const uint InvalidSessionId = 0xFFFFFFFF;

    public uint? ActiveConsoleSessionId
    {
        get
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            return sessionId == InvalidSessionId ? null : sessionId;
        }
    }

    public bool IsInteractiveSessionAvailable(uint sessionId) =>
        ActiveConsoleSessionId is uint activeSessionId && activeSessionId == sessionId;

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint WTSGetActiveConsoleSessionId();
}
