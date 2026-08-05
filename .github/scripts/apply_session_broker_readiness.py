from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8-sig")


def write(relative: str, value: str) -> None:
    path = ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8", newline="\n")


def replace_once(value: str, old: str, new: str, label: str) -> str:
    count = value.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one occurrence, found {count}")
    return value.replace(old, new, 1)


program_path = "src/SirkAgent.Session/Program.cs"
program = read(program_path)
program = replace_once(
    program,
    "    private static readonly ImageCodecInfo JpegEncoder = ImageCodecInfo.GetImageEncoders()\n"
    "        .First(value => value.FormatID == ImageFormat.Jpeg.Guid);\n",
    "    private static readonly Lazy<ImageCodecInfo> JpegEncoder = new(() =>\n"
    "        ImageCodecInfo.GetImageEncoders()\n"
    "            .First(value => value.FormatID == ImageFormat.Jpeg.Guid));\n",
    "lazy JPEG encoder",
)
program = replace_once(
    program,
    "    [STAThread]\n    private static async Task Main()\n    {\n",
    "    [STAThread]\n"
    "    private static async Task<int> Main()\n"
    "    {\n"
    "        try\n"
    "        {\n"
    "            await RunAsync();\n"
    "            return 0;\n"
    "        }\n"
    "        catch (Exception error)\n"
    "        {\n"
    "            LogFatalStartup(error);\n"
    "            return 1;\n"
    "        }\n"
    "    }\n\n"
    "    private static async Task RunAsync()\n"
    "    {\n",
    "fatal startup guard",
)
program = replace_once(
    program,
    "        encodedBitmap.Save(output, JpegEncoder, parameters);\n",
    "        encodedBitmap.Save(output, JpegEncoder.Value, parameters);\n",
    "lazy JPEG encoder use",
)
program = replace_once(
    program,
    "    private static NamedPipeServerStream CreatePipe(string? name = null)\n",
    "    private static void LogFatalStartup(Exception error)\n"
    "    {\n"
    "        LogError(error);\n"
    "        try\n"
    "        {\n"
    "            var directory = Path.Combine(Environment.GetFolderPath(\n"
    "                Environment.SpecialFolder.CommonApplicationData), \"SIRK\", \"Agent\");\n"
    "            Directory.CreateDirectory(directory);\n"
    "            File.AppendAllText(Path.Combine(directory, \"session-startup-error.log\"),\n"
    "                DateTimeOffset.UtcNow.ToString(\"O\") + \" sessionId=\" + SessionId + \" \" +\n"
    "                error + Environment.NewLine);\n"
    "        }\n"
    "        catch { }\n"
    "    }\n\n"
    "    private static NamedPipeServerStream CreatePipe(string? name = null)\n",
    "fatal startup diagnostics",
)
write(program_path, program)

pipe_path = "src/SirkAgent.Service/InteractiveSessionPipe.cs"
pipe = read(pipe_path)
pipe = replace_once(
    pipe,
    "    internal static bool IsAvailable(int sessionId) =>\n"
    "        Process.GetProcessesByName(\"SirkAgent.Session\").Any(process =>\n"
    "        {\n"
    "            try { return process.SessionId == sessionId; }\n"
    "            finally { process.Dispose(); }\n"
    "        });\n",
    "    internal static bool IsAvailable(int sessionId) =>\n"
    "        ProcessExists(sessionId) && PipeReady(sessionId, 0);\n\n"
    "    private static bool ProcessExists(int sessionId) =>\n"
    "        Process.GetProcessesByName(\"SirkAgent.Session\").Any(process =>\n"
    "        {\n"
    "            try { return process.SessionId == sessionId; }\n"
    "            finally { process.Dispose(); }\n"
    "        });\n\n"
    "    private static bool PipeReady(int sessionId, uint timeoutMilliseconds) =>\n"
    "        WaitNamedPipe(@\"\\\\.\\pipe\\\" + Name(sessionId), timeoutMilliseconds);\n",
    "pipe-based readiness",
)
new_ensure = r'''    internal static bool EnsureAvailable(int sessionId)
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
'''
pipe, count = re.subn(
    r"    internal static bool EnsureAvailable\(int sessionId\)\n    \{[\s\S]*?\n    \}\n\n    private static int\? ResolveActiveSession\(\)",
    new_ensure + "\n    private static int? ResolveActiveSession()",
    pipe,
    count=1,
)
if count != 1:
    raise RuntimeError(f"EnsureAvailable replacement: expected one block, found {count}")
pipe = replace_once(
    pipe,
    "    [DllImport(\"kernel32.dll\")]\n"
    "    private static extern bool CloseHandle(IntPtr handle);\n",
    "    [DllImport(\"kernel32.dll\", CharSet = CharSet.Unicode, SetLastError = true)]\n"
    "    [return: MarshalAs(UnmanagedType.Bool)]\n"
    "    private static extern bool WaitNamedPipe(string name, uint timeoutMilliseconds);\n\n"
    "    [DllImport(\"kernel32.dll\", SetLastError = true)]\n"
    "    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);\n\n"
    "    [DllImport(\"kernel32.dll\", SetLastError = true)]\n"
    "    [return: MarshalAs(UnmanagedType.Bool)]\n"
    "    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);\n\n"
    "    [DllImport(\"kernel32.dll\")]\n"
    "    private static extern bool CloseHandle(IntPtr handle);\n",
    "broker readiness Win32 APIs",
)
write(pipe_path, pipe)

contract = r'''#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pipe = Get-Content (Join-Path $root 'src\SirkAgent.Service\InteractiveSessionPipe.cs') -Raw
$session = Get-Content (Join-Path $root 'src\SirkAgent.Session\Program.cs') -Raw

foreach ($required in @(
    'WaitNamedPipe',
    'PipeReady(sessionId, 100)',
    'WaitForSingleObject(process.Process, 0)',
    'GetExitCodeProcess',
    'session-startup-error.log'
)) {
    if ($pipe.IndexOf($required, [StringComparison]::Ordinal) -lt 0) {
        throw "Session broker readiness contract missing: $required"
    }
}

if ($pipe -match 'internal static bool IsAvailable\(int sessionId\)\s*=>\s*Process\.GetProcessesByName') {
    throw 'Session broker readiness still relies only on process enumeration.'
}
if ($session -notmatch 'Lazy<ImageCodecInfo> JpegEncoder') {
    throw 'JPEG codec initialization is still eager during broker startup.'
}
if ($session -notmatch 'private static async Task<int> Main\(\)') {
    throw 'Session broker fatal startup guard is missing.'
}
if ($session -notmatch 'LogFatalStartup\(error\)') {
    throw 'Session broker fatal startup diagnostics are missing.'
}

Write-Host 'SESSION_BROKER_READINESS_CONTRACT_OK'
'''
write("tests/session-broker-readiness-contract.ps1", contract)

print("Session broker readiness and startup diagnostics applied.")
