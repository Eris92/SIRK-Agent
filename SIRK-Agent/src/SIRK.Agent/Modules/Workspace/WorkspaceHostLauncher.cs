using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sirk.Agent.Modules.Workspace;

internal interface IWorkspaceHostLauncher
{
    bool IsSupported { get; }

    WorkspaceHostLaunchResult Launch(uint sessionId, string workspaceHostPath, string pipeName);
}

internal sealed record WorkspaceHostLaunchResult(
    bool Success,
    int? ProcessId,
    string? PipeName,
    string? Token,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed class WindowsWorkspaceHostLauncher : IWorkspaceHostLauncher
{
    private const uint TokenAllAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    public bool IsSupported => OperatingSystem.IsWindows();

    public WorkspaceHostLaunchResult Launch(uint sessionId, string workspaceHostPath, string pipeName)
    {
        if (sessionId == 0)
        {
            return Failure("session_zero_blocked", "WorkspaceHost cannot be launched in Windows Session 0.");
        }

        if (string.IsNullOrWhiteSpace(workspaceHostPath) || !Path.IsPathFullyQualified(workspaceHostPath))
        {
            return Failure("workspace_host_path_invalid", "WorkspaceHost path must be an absolute path.");
        }

        if (!File.Exists(workspaceHostPath))
        {
            return Failure("workspace_host_not_found", "WorkspaceHost executable is not installed.");
        }

        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128 || pipeName.Contains('\\') || pipeName.Contains('/'))
        {
            return Failure("workspace_host_pipe_invalid", "WorkspaceHost pipe name is invalid.");
        }

        string token = CreateBase64UrlToken();
        string commandLine = BuildCommandLine(workspaceHostPath, sessionId, pipeName, token);

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        PROCESS_INFORMATION processInformation = default;

        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                return Win32Failure("wts_query_user_token_failed");
            }

            if (!DuplicateTokenEx(userToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                return Win32Failure("duplicate_user_token_failed");
            }

            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                return Win32Failure("environment_block_failed");
            }

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default"
            };

            var mutableCommandLine = new StringBuilder(commandLine);
            bool created = CreateProcessAsUser(
                primaryToken,
                workspaceHostPath,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateUnicodeEnvironment | CreateNoWindow,
                environment,
                Path.GetDirectoryName(workspaceHostPath),
                ref startupInfo,
                out processInformation);

            if (!created)
            {
                return Win32Failure("create_process_as_user_failed");
            }

            return new WorkspaceHostLaunchResult(
                true,
                checked((int)processInformation.dwProcessId),
                pipeName,
                token,
                null,
                null);
        }
        finally
        {
            if (processInformation.hThread != IntPtr.Zero)
            {
                CloseHandle(processInformation.hThread);
            }

            if (processInformation.hProcess != IntPtr.Zero)
            {
                CloseHandle(processInformation.hProcess);
            }

            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != IntPtr.Zero)
            {
                CloseHandle(primaryToken);
            }

            if (userToken != IntPtr.Zero)
            {
                CloseHandle(userToken);
            }
        }
    }

    private static string BuildCommandLine(string executablePath, uint sessionId, string pipeName, string token) =>
        $"\"{executablePath}\" --session-id {sessionId} --pipe-name {pipeName} --token {token}";

    private static string CreateBase64UrlToken()
    {
        string value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static WorkspaceHostLaunchResult Win32Failure(string errorCode)
    {
        int error = Marshal.GetLastWin32Error();
        string systemMessage = new Win32Exception(error).Message;
        return Failure(errorCode, $"WorkspaceHost launch failed with Windows error {error}: {systemMessage}");
    }

    private static WorkspaceHostLaunchResult Failure(string code, string message) =>
        new(false, null, null, null, code, message);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        internal int cb;
        internal string? lpReserved;
        internal string? lpDesktop;
        internal string? lpTitle;
        internal int dwX;
        internal int dwY;
        internal int dwXSize;
        internal int dwYSize;
        internal int dwXCountChars;
        internal int dwYCountChars;
        internal int dwFillAttribute;
        internal int dwFlags;
        internal short wShowWindow;
        internal short cbReserved2;
        internal IntPtr lpReserved2;
        internal IntPtr hStdInput;
        internal IntPtr hStdOutput;
        internal IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        internal IntPtr hProcess;
        internal IntPtr hThread;
        internal uint dwProcessId;
        internal uint dwThreadId;
    }
}