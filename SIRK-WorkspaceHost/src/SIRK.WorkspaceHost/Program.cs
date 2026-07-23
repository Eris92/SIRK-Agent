using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

const int MaximumMessageBytes = 16 * 1024;

WorkspaceHostOptions? options = WorkspaceHostOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: SIRK-WorkspaceHost --session-id <id> --pipe-name <name> --token <base64url>");
    return 2;
}

if (!NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out uint currentSessionId))
{
    Console.Error.WriteLine("Unable to resolve the current Windows session.");
    return 3;
}

if (currentSessionId == 0 || currentSessionId != options.SessionId)
{
    Console.Error.WriteLine("WorkspaceHost session validation failed.");
    return 4;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

try
{
    await using var pipe = new NamedPipeClientStream(
        ".",
        options.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous,
        TokenImpersonationLevel.Identification);

    await pipe.ConnectAsync(timeout.Token);

    byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new
    {
        protocolVersion = 1,
        messageType = "WorkspaceHost.Hello",
        sessionId = options.SessionId,
        processId = Environment.ProcessId,
        token = options.Token
    });

    await WriteFrameAsync(pipe, hello, timeout.Token);
    byte[] acknowledgement = await ReadFrameAsync(pipe, timeout.Token);

    using JsonDocument response = JsonDocument.Parse(acknowledgement);
    if (!response.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
    {
        Console.Error.WriteLine("WorkspaceHost handshake was rejected.");
        return 5;
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("WorkspaceHost handshake timed out.");
    return 6;
}
catch (IOException)
{
    Console.Error.WriteLine("WorkspaceHost IPC is unavailable.");
    return 7;
}
catch (JsonException)
{
    Console.Error.WriteLine("WorkspaceHost received an invalid acknowledgement.");
    return 8;
}

static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
{
    if (payload.Length is <= 0 or > MaximumMessageBytes)
    {
        throw new InvalidDataException("WorkspaceHost message length is outside the allowed limit.");
    }

    byte[] header = new byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(payload, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
{
    byte[] header = new byte[sizeof(int)];
    await stream.ReadExactlyAsync(header, cancellationToken);

    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length is <= 0 or > MaximumMessageBytes)
    {
        throw new InvalidDataException("WorkspaceHost response length is outside the allowed limit.");
    }

    byte[] payload = new byte[length];
    await stream.ReadExactlyAsync(payload, cancellationToken);
    return payload;
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}

internal sealed record WorkspaceHostOptions(uint SessionId, string PipeName, string Token)
{
    internal static WorkspaceHostOptions? Parse(string[] arguments)
    {
        if (arguments.Length != 6)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            if (!values.TryAdd(arguments[index], arguments[index + 1]))
            {
                return null;
            }
        }

        if (!values.TryGetValue("--session-id", out string? sessionValue) ||
            !uint.TryParse(sessionValue, out uint sessionId) ||
            sessionId == 0)
        {
            return null;
        }

        if (!values.TryGetValue("--pipe-name", out string? pipeName) ||
            string.IsNullOrWhiteSpace(pipeName) ||
            pipeName.Length > 128 ||
            pipeName.Contains('\\') ||
            pipeName.Contains('/'))
        {
            return null;
        }

        if (!values.TryGetValue("--token", out string? token) || !IsValidToken(token))
        {
            return null;
        }

        return new WorkspaceHostOptions(sessionId, pipeName, token);
    }

    private static bool IsValidToken(string token)
    {
        if (token.Length is < 43 or > 128)
        {
            return false;
        }

        foreach (char character in token)
        {
            bool valid =
                character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '-' or '_';

            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}
