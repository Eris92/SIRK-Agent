using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

const string pipeName = "SIRK.Agent.v1";
const int maximumMessageBytes = 64 * 1024;

string agentPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : throw new ArgumentException("Agent executable path is required.");

if (!File.Exists(agentPath))
{
    throw new FileNotFoundException("SIRK-Agent executable was not found.", agentPath);
}

using Process agent = Process.Start(new ProcessStartInfo
{
    FileName = agentPath,
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true
}) ?? throw new InvalidOperationException("Unable to start SIRK-Agent.");

try
{
    string requestId = Guid.NewGuid().ToString("D");
    string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    DateTimeOffset now = DateTimeOffset.UtcNow;

    byte[] command = JsonSerializer.SerializeToUtf8Bytes(new
    {
        protocolVersion = 1,
        messageType = "System.Ping",
        requestId,
        deviceId = Environment.MachineName,
        operatorId = "integration-test",
        issuedAt = now,
        expiresAt = now.AddSeconds(30),
        nonce,
        payload = new { }
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    JsonDocument first = await SendAsync(command, TimeSpan.FromSeconds(10));
    Assert(first.RootElement.GetProperty("ok").GetBoolean(), "First System.Ping did not succeed.");
    Assert(first.RootElement.GetProperty("result").GetProperty("message").GetString() == "pong", "Unexpected ping response.");

    JsonDocument replay = await SendAsync(command, TimeSpan.FromSeconds(5));
    Assert(!replay.RootElement.GetProperty("ok").GetBoolean(), "Replay request was accepted.");
    Assert(replay.RootElement.GetProperty("error").GetProperty("code").GetString() == "replay_detected", "Unexpected replay error code.");

    Console.WriteLine("SIRK-Agent integration tests passed: ping and replay protection.");
}
finally
{
    if (!agent.HasExited)
    {
        agent.Kill(entireProcessTree: true);
        await agent.WaitForExitAsync();
    }
}

static async Task<JsonDocument> SendAsync(byte[] payload, TimeSpan timeoutValue)
{
    using var timeout = new CancellationTokenSource(timeoutValue);
    await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(timeout.Token);

    byte[] header = new byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
    await pipe.WriteAsync(header, timeout.Token);
    await pipe.WriteAsync(payload, timeout.Token);
    await pipe.FlushAsync(timeout.Token);

    await pipe.ReadExactlyAsync(header, timeout.Token);
    int responseLength = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (responseLength <= 0 || responseLength > maximumMessageBytes)
    {
        throw new InvalidDataException("Invalid IPC response length.");
    }

    byte[] response = new byte[responseLength];
    await pipe.ReadExactlyAsync(response, timeout.Token);
    return JsonDocument.Parse(response);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
