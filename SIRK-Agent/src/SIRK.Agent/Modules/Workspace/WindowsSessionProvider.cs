using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sirk.Agent.Modules.Workspace;

internal sealed class WindowsSessionProvider : IWindowsSessionProvider
{
    private const uint InvalidSessionId = 0xFFFFFFFF;
    private static readonly nint CurrentServerHandle = 0;

    public uint? ActiveConsoleSessionId
    {
        get
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            return sessionId == InvalidSessionId ? null : sessionId;
        }
    }

    public IReadOnlyList<WindowsSessionInfo> GetSessions()
    {
        if (!WTSEnumerateSessionsW(CurrentServerHandle, 0, 1, out nint buffer, out int count))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate Windows sessions.");
        }

        try
        {
            var sessions = new List<WindowsSessionInfo>(Math.Max(count, 0));
            int entrySize = Marshal.SizeOf<WtsSessionInfo>();

            for (int index = 0; index < count; index++)
            {
                nint entryPointer = nint.Add(buffer, index * entrySize);
                WtsSessionInfo entry = Marshal.PtrToStructure<WtsSessionInfo>(entryPointer);
                string stationName = Marshal.PtrToStringUni(entry.StationName) ?? string.Empty;
                string state = entry.State.ToString();
                bool isInteractive = entry.SessionId != 0 && entry.State == WtsConnectState.Active;

                sessions.Add(new WindowsSessionInfo(
                    entry.SessionId,
                    stationName,
                    state,
                    isInteractive));
            }

            return sessions;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public bool IsInteractiveSessionAvailable(uint sessionId) =>
        GetSessions().Any(session => session.SessionId == sessionId && session.IsInteractive);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        nint serverHandle,
        int reserved,
        int version,
        out nint sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WtsSessionInfo
    {
        internal readonly uint SessionId;
        internal readonly nint StationName;
        internal readonly WtsConnectState State;
    }

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init
    }
}