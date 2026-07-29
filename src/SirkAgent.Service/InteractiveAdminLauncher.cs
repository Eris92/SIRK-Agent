using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace SirkAgent.Service;

internal sealed record AdministrativeDesktopProcess(int ProcessId, int SessionId, string Tool);

internal static class InteractiveAdminLauncher
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint MaximumAllowed = 0x02000000;
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;

    internal static (string Application, string Arguments) ResolveTool(string value)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return value.ToLowerInvariant() switch
        {
            "powershell" => (powershell, "-NoLogo -NoProfile -NoExit"),
            "computer-management" => (Path.Combine(system, "mmc.exe"), "compmgmt.msc"),
            "services" => (Path.Combine(system, "mmc.exe"), "services.msc"),
            "registry" => (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "regedit.exe"), ""),
            "task-manager" => (Path.Combine(system, "taskmgr.exe"), ""),
            "event-viewer" => (Path.Combine(system, "mmc.exe"), "eventvwr.msc"),
            "device-manager" => (Path.Combine(system, "mmc.exe"), "devmgmt.msc"),
            _ => throw new InvalidDataException("Niedozwolone narzędzie administracyjne.")
        };
    }

    internal static AdministrativeDesktopProcess Start(int sessionId, string tool)
    {
        if (WindowsIdentity.GetCurrent().User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            throw new UnauthorizedAccessException("Tryb administracyjny wymaga usługi LocalSystem.");
        if (!InteractiveSessionPipe.IsAvailable(sessionId))
            throw new InvalidDataException("Wybrana sesja użytkownika nie ma aktywnego brokera.");

        var resolved = ResolveTool(tool);
        if (!File.Exists(resolved.Application))
            throw new FileNotFoundException("Narzędzie administracyjne nie istnieje.", resolved.Application);

        var currentToken = IntPtr.Zero;
        var primaryToken = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var processInfo = new ProcessInformation();
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId,
                    out currentToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!DuplicateTokenEx(currentToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation,
                    TokenPrimary, out primaryToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var targetSession = sessionId;
            if (!SetTokenInformation(primaryToken, TokenSessionId, ref targetSession, sizeof(int)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default",
                ShowWindow = 1
            };
            var commandLine = new StringBuilder("\"" + resolved.Application + "\"" +
                                                (string.IsNullOrWhiteSpace(resolved.Arguments)
                                                    ? "" : " " + resolved.Arguments));
            if (!CreateProcessAsUser(primaryToken, resolved.Application, commandLine, IntPtr.Zero, IntPtr.Zero,
                    false, CreateUnicodeEnvironment | CreateNewConsole, environment,
                    Path.GetDirectoryName(resolved.Application), ref startup, out processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new AdministrativeDesktopProcess(processInfo.ProcessId, sessionId, tool);
        }
        finally
        {
            if (processInfo.Thread != IntPtr.Zero) CloseHandle(processInfo.Thread);
            if (processInfo.Process != IntPtr.Zero) CloseHandle(processInfo.Process);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (currentToken != IntPtr.Zero) CloseHandle(currentToken);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int informationClass,
        ref int information, int informationLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfo startup,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
