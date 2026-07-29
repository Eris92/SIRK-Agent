using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SirkAgent.Service;

internal sealed class RemoteCommandExecutor
{
    private const int MaximumOutputBytes = 1024 * 1024;
    private readonly JsonSerializerOptions _json;

    public RemoteCommandExecutor(JsonSerializerOptions json) => _json = json;

    public async Task<RemoteCommandResult> ExecuteAsync(PortalRemoteCommand command, JsonElement? activePolicy,
        CancellationToken token)
    {
        try
        {
            if (DateTimeOffset.UtcNow > command.ExpiresAtUtc)
                return Failure(command.CommandId, "COMMAND_EXPIRED", "Polecenie wygasło.");
            return command.Type switch
            {
                "terminal.execute" when Enabled(activePolicy, "remoteTerminalEnabled") =>
                    await ExecuteTerminalAsync(command, token),
                "files.list" when Enabled(activePolicy, "remoteFilesEnabled") =>
                    ListFiles(command),
                "files.read" when Enabled(activePolicy, "remoteFilesEnabled") =>
                    await ReadFileAsync(command, token),
                "files.write" when Enabled(activePolicy, "remoteFilesEnabled") =>
                    await WriteFileAsync(command, token),
                "desktop.sessions" when Enabled(activePolicy, "remoteDesktopEnabled") =>
                    new RemoteCommandResult(command.CommandId, true, "DESKTOP_SESSIONS_OK", "",
                        ToElement(InteractiveSessionPipe.Sessions())),
                "desktop.monitors" when Enabled(activePolicy, "remoteDesktopEnabled") =>
                    await ExecuteDesktopAsync(command, "monitors", token),
                "desktop.admin.start" when Enabled(activePolicy, "remoteAdministrativeDesktopEnabled") ||
                                           Enabled(activePolicy, "remoteDesktopEnabled") =>
                    StartAdministrativeDesktop(command),
                "desktop.snapshot" when Enabled(activePolicy, "remoteDesktopEnabled") =>
                    await ExecuteDesktopAsync(command, "snapshot", token),
                "desktop.input" when Enabled(activePolicy, "remoteDesktopEnabled") =>
                    await ExecuteDesktopAsync(command, "input", token),
                _ => Failure(command.CommandId, "OPERATION_NOT_ALLOWED",
                    "Operacja jest wyłączona przez podpisaną politykę urządzenia.")
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(command.CommandId, "OPERATION_FAILED", ex.Message);
        }
    }

    private async Task<RemoteCommandResult> ExecuteDesktopAsync(PortalRemoteCommand command, string type,
        CancellationToken token)
    {
        var sessionId = InteractiveSessionPipe.Resolve(OptionalNullableInt(command.Parameters, "sessionId"));
        if (InteractiveSessionPipe.EnsureAvailable(sessionId))
            InteractiveSessionClient.Invalidate(sessionId);
        string? responseLine;
        try
        {
            object request = new
            {
                type,
                action = OptionalString(command.Parameters, "action", "", 32),
                x = OptionalInt(command.Parameters, "x", 0),
                y = OptionalInt(command.Parameters, "y", 0),
                delta = OptionalInt(command.Parameters, "delta", 0),
                monitorIndex = OptionalInt(command.Parameters, "monitorIndex", -1),
                maxWidth = Math.Clamp(OptionalInt(command.Parameters, "maxWidth", 1280), 640, 1920),
                quality = Math.Clamp(OptionalInt(command.Parameters, "quality", 40), 25, 80),
                text = OptionalString(command.Parameters, "text", "", MaximumOutputBytes),
                key = OptionalString(command.Parameters, "key", "", 32),
                modifiers = OptionalString(command.Parameters, "modifiers", "", 64)
            };
            responseLine = await InteractiveSessionClient.SendAsync(sessionId,
                JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Failure(command.CommandId, "INTERACTIVE_HELPER_UNAVAILABLE",
                "Broker aktywnej sesji użytkownika nie odpowiada.");
        }
        if (string.IsNullOrWhiteSpace(responseLine))
            return Failure(command.CommandId, "INTERACTIVE_HELPER_INVALID", "Broker nie zwrócił odpowiedzi.");
        using var response = JsonDocument.Parse(responseLine);
        var root = response.RootElement;
        var ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
        var code = root.TryGetProperty("code", out var codeValue) ? codeValue.GetString() ?? "" : "";
        var output = root.TryGetProperty("error", out var errorValue) &&
            errorValue.ValueKind == JsonValueKind.String ? errorValue.GetString() ?? "" : "";
        return new RemoteCommandResult(command.CommandId, ok, code, output,
            ok ? root.Clone() : null);
    }

    private RemoteCommandResult StartAdministrativeDesktop(PortalRemoteCommand command)
    {
        var sessionId = InteractiveSessionPipe.Resolve(OptionalNullableInt(command.Parameters, "sessionId"));
        if (InteractiveSessionPipe.EnsureAvailable(sessionId))
            InteractiveSessionClient.Invalidate(sessionId);
        var tool = OptionalString(command.Parameters, "tool", "powershell", 64);
        var process = InteractiveAdminLauncher.Start(sessionId, tool);
        return new RemoteCommandResult(command.CommandId, true, "DESKTOP_ADMIN_STARTED", "",
            ToElement(new { process.ProcessId, process.SessionId, process.Tool }));
    }

    private async Task<RemoteCommandResult> ExecuteTerminalAsync(PortalRemoteCommand command,
        CancellationToken token)
    {
        var script = RequiredString(command.Parameters, "command", 16 * 1024);
        var timeoutSeconds = Math.Clamp(OptionalInt(command.Parameters, "timeoutSeconds", 30), 1, 120);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("RemoteSigned");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Nie uruchomiono PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return Failure(command.CommandId, "TERMINAL_TIMEOUT", "Przekroczono limit czasu polecenia.");
        }
        var output = Limit((await stdout) + (await stderr));
        return new RemoteCommandResult(command.CommandId, process.ExitCode == 0,
            process.ExitCode == 0 ? "TERMINAL_OK" : "TERMINAL_EXIT_" + process.ExitCode, output, null);
    }

    private RemoteCommandResult ListFiles(PortalRemoteCommand command)
    {
        var target = ExistingPath(command.Parameters, "path", requireFile: false);
        var entries = Directory.EnumerateFileSystemEntries(target).Take(1000).Select(value =>
        {
            var info = (FileSystemInfo)(Directory.Exists(value) ? new DirectoryInfo(value) : new FileInfo(value));
            return new
            {
                name = info.Name,
                path = info.FullName,
                isDirectory = info is DirectoryInfo,
                length = info is FileInfo file ? file.Length : 0,
                lastWriteUtc = info.LastWriteTimeUtc
            };
        }).ToArray();
        return new RemoteCommandResult(command.CommandId, true, "FILES_LIST_OK", "", ToElement(entries));
    }

    private async Task<RemoteCommandResult> ReadFileAsync(PortalRemoteCommand command, CancellationToken token)
    {
        var target = ExistingPath(command.Parameters, "path", requireFile: true);
        var info = new FileInfo(target);
        if (info.Length > MaximumOutputBytes) throw new InvalidDataException("Plik przekracza limit 1 MiB.");
        var data = await File.ReadAllBytesAsync(target, token);
        return new RemoteCommandResult(command.CommandId, true, "FILE_READ_OK", "",
            ToElement(new { name = info.Name, path = info.FullName, contentBase64 = Convert.ToBase64String(data) }));
    }

    private async Task<RemoteCommandResult> WriteFileAsync(PortalRemoteCommand command, CancellationToken token)
    {
        var target = NormalizedPath(RequiredString(command.Parameters, "path", 32767));
        var content = Convert.FromBase64String(RequiredString(command.Parameters, "contentBase64",
            MaximumOutputBytes * 2));
        if (content.Length > MaximumOutputBytes) throw new InvalidDataException("Plik przekracza limit 1 MiB.");
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("Katalog docelowy nie istnieje.");
        await File.WriteAllBytesAsync(target, content, token);
        return new RemoteCommandResult(command.CommandId, true, "FILE_WRITE_OK", "",
            ToElement(new { path = target, length = content.Length }));
    }

    private JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, _json);
    private static bool Enabled(JsonElement? policy, string key) =>
        policy is { ValueKind: JsonValueKind.Object } &&
        policy.Value.TryGetProperty("settings", out var settings) &&
        settings.ValueKind == JsonValueKind.Object &&
        settings.TryGetProperty(key, out var enabled) &&
        enabled.ValueKind == JsonValueKind.True;

    private static string ExistingPath(JsonElement parameters, string name, bool requireFile)
    {
        var value = NormalizedPath(RequiredString(parameters, name, 32767));
        if (requireFile ? !File.Exists(value) : !Directory.Exists(value))
            throw new FileNotFoundException("Ścieżka nie istnieje.", value);
        return value;
    }

    private static string NormalizedPath(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new InvalidDataException("Wymagana jest pełna ścieżka.");
        return Path.GetFullPath(value);
    }

    private static string RequiredString(JsonElement value, string name, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException("Brak parametru: " + name);
        var result = property.GetString()!;
        if (result.Length > maximumLength) throw new InvalidDataException("Parametr jest zbyt długi: " + name);
        return result;
    }

    private static int OptionalInt(JsonElement value, string name, int fallback) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.TryGetInt32(out var result) ? result : fallback;

    private static int? OptionalNullableInt(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.TryGetInt32(out var result) ? result : null;

    private static string OptionalString(JsonElement value, string name, string fallback, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String) return fallback;
        var result = property.GetString() ?? fallback;
        return result.Length <= maximumLength ? result : fallback;
    }

    private static string Limit(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return bytes.Length <= MaximumOutputBytes ? value :
            Encoding.UTF8.GetString(bytes.AsSpan(0, MaximumOutputBytes)) + "\n[OUTPUT_TRUNCATED]";
    }

    private static RemoteCommandResult Failure(string id, string code, string message) =>
        new(id, false, code, message, null);
}

internal sealed record PortalRemoteCommand(string CommandId, string Type, JsonElement Parameters,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);
internal sealed record RemoteCommandResult(string CommandId, bool Ok, string Code, string Output,
    JsonElement? Data);
