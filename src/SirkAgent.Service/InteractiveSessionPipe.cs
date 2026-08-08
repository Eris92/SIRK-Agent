using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SirkAgent.Service;

internal static class InteractiveSessionPipe
{
    private sealed record SessionDescriptor(int sessionId, bool active, bool helperAvailable, int processId);

    private const string Prefix = "SIRK-Agent-Interactive-Session-";
    private static readonly object LaunchSync = new();

    internal static string Name(int sessionId) => Prefix + sessionId;
    internal static string ActiveName() => Name(Resolve(null));

    internal static int Resolve(int? requested)
    {
        if (requested is >= 0 and <= 65535) return requested.Value;
        if (requested is not null) throw new InvalidDataException("Nieprawidłowy identyfikator sesji.");
        return ResolveActiveSession()
               ?? throw new InvalidOperationException("Brak aktywnej interaktywnej sesji użytkownika.");
    }

    internal static object[] Sessions()
    {
        var active = ResolveActiveSession();
        var helperAvailable = false;
        if (active is not null)
        {
            try
            {
                EnsureAvailable(active.Value);
                helperAvailable = true;
            }
            catch
            {
                helperAvailable = IsAvailable(active.Value);
            }
        }

        var sessions = Process.GetProcessesByName("SirkAgent.Session")
            .Select(process =>
            {
                try
                {
                    return new SessionDescriptor(
                        process.SessionId,
                        active is not null && process.SessionId == active.Value,
                        true,
                        process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            })
            .GroupBy(item => item.sessionId)
            .Select(group => group.First())
            .OrderBy(item => item.sessionId)
            .ToList();

        if (active is not null && sessions.All(item => item.sessionId != active.Value))
            sessions.Add(new SessionDescriptor(active.Value, true, helperAvailable, 0));

        return sessions.OrderBy(item => item.sessionId).Cast<object>().ToArray();
    }

    internal static bool IsAvailable(int sessionId) =>
        ProcessExists(sessionId) && PipeReady(sessionId, 0);

    private static bool ProcessExists(int sessionId) =>
        Process.GetProcessesByName("SirkAgent.Session").Any(process =>
        {
            try { return process.SessionId == sessionId; }
            finally { process.Dispose(); }
        });

    private static bool PipeReady(int sessionId, uint timeoutMilliseconds) =>
        WaitNamedPipe(@"\\.\pipe\" + Name(sessionId), timeoutMilliseconds);

    internal static void Terminate(int sessionId)
    {
        foreach (var process in Process.GetProcessesByName("SirkAgent.Session"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId) continue;
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException) { }
            }
        }
    }

    internal static void TerminateAll()
    {
        lock (LaunchSync)
        {
            foreach (var process in Process.GetProcessesByName("SirkAgent.Session"))
            {
                using (process)
                {
                    try
                    {
                        process.Kill(true);
                        process.WaitForExit(5000);
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }
    }

    internal static bool EnsureAvailable(int sessionId)
    {
        lock (LaunchSync)
        {
            if (IsAvailable(sessionId)) return false;
            if (ProcessExists(sessionId)) Terminate(sessionId);

            var executable = Path.Combine(AppContext.BaseDirectory, "Session", "SirkAgent.Session.exe");
            if (!File.Exists(executable))
                executable = Path.Combine(AppContext.BaseDirectory, "SirkAgent.Session.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("Brak brokera sesji użytkownika.", executable);
            if (!WTSQueryUserToken((uint)sessionId, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Nie można otworzyć aktywnej sesji użytkownika.");

            var environment = IntPtr.Zero;
            var process = new ProcessInformation();
            try
            {
                if (!CreateEnvironmentBlock(out environment, token, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfo>(),
                    Desktop = @"winsta0\default"
                };
                var command = new StringBuilder("\"" + executable + "\"");
                if (!CreateProcessAsUser(token, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                        0x00000400, environment, Path.GetDirectoryName(executable)!,
                        ref startup, out process))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Nie można uruchomić brokera sesji użytkownika.");
                }

                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    if (PipeReady(sessionId, 100)) return true;
                    if (process.Process != IntPtr.Zero && WaitForSingleObject(process.Process, 0) == 0)
                    {
                        _ = GetExitCodeProcess(process.Process, out var exitCode);
                        throw new InvalidOperationException(
                            $"Broker sesji użytkownika zakończył się podczas startu. " +
                            $"SessionId={sessionId}; ProcessId={process.ProcessId}; ExitCode={exitCode}; " +
                            @"Log=C:\ProgramData\SIRK\Agent\session-startup-error.log");
                    }
                    Thread.Sleep(25);
                }

                throw new TimeoutException(
                    $"Broker sesji użytkownika nie otworzył kanału sterowania. " +
                    $"SessionId={sessionId}; ProcessId={process.ProcessId}; " +
                    @"Log=C:\ProgramData\SIRK\Agent\session-startup-error.log");
            }
            finally
            {
                if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
                if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }
    }

    private static int? ResolveActiveSession()
    {
        var console = WTSGetActiveConsoleSessionId();
        if (console != uint.MaxValue && CanOpenUserToken(console))
            return checked((int)console);

        foreach (var sessionId in EnumerateActiveWtsSessions())
        {
            if (CanOpenUserToken((uint)sessionId))
                return sessionId;
        }

        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId > 0 && CanOpenUserToken((uint)process.SessionId))
                        return process.SessionId;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        return null;
    }

    private static IEnumerable<int> EnumerateActiveWtsSessions()
    {
        if (!WTSEnumerateSessions(
                IntPtr.Zero,
                0,
                1,
                out var sessionInfo,
                out var count))
        {
            yield break;
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var pointer = IntPtr.Add(sessionInfo, index * size);
                var session = Marshal.PtrToStructure<WtsSessionInfo>(pointer);
                if (session.State == WtsConnectState.Active && session.SessionId is >= 0 and <= 65535)
                    yield return session.SessionId;
            }
        }
        finally
        {
            WTSFreeMemory(sessionInfo);
        }
    }

    private static bool CanOpenUserToken(uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token)) return false;
        CloseHandle(token);
        return true;
    }

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Pointer, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
