using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SirkAgent.Service;

internal static class InteractiveSessionPipe
{
    private sealed record SessionDescriptor(int sessionId, bool active, bool helperAvailable, int processId);

    private const string Prefix = "SIRK-Agent-Interactive-Session-";

    internal static string Name(int sessionId) => Prefix + sessionId;
    internal static string ActiveName() => Name(Resolve(null));

    internal static int Resolve(int? requested)
    {
        if (requested is >= 0 and <= 65535) return requested.Value;
        if (requested is not null) throw new InvalidDataException("Nieprawidłowy identyfikator sesji.");
        var active = WTSGetActiveConsoleSessionId();
        if (active == uint.MaxValue) throw new InvalidOperationException("Brak aktywnej sesji konsoli.");
        return checked((int)active);
    }

    internal static object[] Sessions()
    {
        var active = WTSGetActiveConsoleSessionId();
        return Process.GetProcessesByName("SirkAgent.Session")
            .Select(process =>
            {
                try
                {
                    return new SessionDescriptor(
                        process.SessionId,
                        active != uint.MaxValue && process.SessionId == (int)active,
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
            .Cast<object>()
            .ToArray();
    }

    internal static bool IsAvailable(int sessionId) =>
        Process.GetProcessesByName("SirkAgent.Session").Any(process =>
        {
            try { return process.SessionId == sessionId; }
            finally { process.Dispose(); }
        });

    internal static void EnsureAvailable(int sessionId)
    {
        if (IsAvailable(sessionId)) return;
        var executable = Path.Combine(AppContext.BaseDirectory, "SirkAgent.Session.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Brak brokera sesji użytkownika.", executable);
        if (!WTSQueryUserToken((uint)sessionId, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Nie można otworzyć aktywnej sesji użytkownika.");
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
                    0x00000400, environment, AppContext.BaseDirectory, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Nie można uruchomić brokera sesji użytkownika.");
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (IsAvailable(sessionId)) return;
            Thread.Sleep(25);
        }
        throw new InvalidOperationException("Broker sesji użytkownika nie uruchomił się.");
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
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
