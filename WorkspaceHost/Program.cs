using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using WorkspaceCommon;

const string Version = "0.1.0";
string pipeName = GetArg("--pipe") ?? "SirK.Workspace";
string sessionToken = GetArg("--session") ?? Guid.NewGuid().ToString("N");
int heartbeatSeconds = int.TryParse(GetArg("--heartbeat"), out var parsed) ? Math.Clamp(parsed, 1, 60) : 5;
string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SirK", "Workspace", "Logs");
Directory.CreateDirectory(logDirectory);
string logPath = Path.Combine(logDirectory, "workspace.log");
var started = Stopwatch.StartNew();
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

Log($"WorkspaceHost {Version} starting; pipe={pipeName}; token={sessionToken}");

while (!shutdown.IsCancellationRequested)
{
    try
    {
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Log("Waiting for pipe client");
        await pipe.WaitForConnectionAsync(shutdown.Token);
        Log("Pipe client connected");

        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        while (pipe.IsConnected && !shutdown.IsCancellationRequested)
        {
            var status = new WorkspaceStatus(
                "heartbeat",
                sessionToken,
                Environment.ProcessId,
                Process.GetCurrentProcess().SessionId,
                WindowsIdentity.GetCurrent().Name,
                "Default",
                Version,
                DateTimeOffset.UtcNow,
                (long)started.Elapsed.TotalSeconds);

            await writer.WriteLineAsync(JsonSerializer.Serialize(status));
            await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), shutdown.Token);
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    catch (Exception ex)
    {
        Log($"ERROR {ex}");
        try { await Task.Delay(2000, shutdown.Token); } catch (OperationCanceledException) { }
    }
}

Log("WorkspaceHost stopped");
return;

string? GetArg(string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

void Log(string message)
{
    var line = $"{DateTimeOffset.Now:O} [PID:{Environment.ProcessId}] {message}";
    Console.WriteLine(line);
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
}
