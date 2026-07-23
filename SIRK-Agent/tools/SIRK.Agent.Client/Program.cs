using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

const string pipeName = "SIRK.Agent.v1";
const int maximumMessageBytes = 64 * 1024;

string messageType = args.Length > 0 ? args[0] : "System.Ping";
string deviceId = args.Length > 1 ? args[1] : Environment.MachineName;
string operatorId = args.Length > 2 ? args[2] : $"local:{Environment.UserName}";

DateTimeOffset now = DateTimeOffset.UtcNow;
var request = new
{
    protocolVersion = 1,
    messageType,
    requestId = Guid.NewGuid().ToString("D"),
    deviceId,
    operatorId,
    issuedAt = now,
    expiresAt = now.AddSeconds(30),
    nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
    payload = new { }
};

byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
if (requestBytes.Length > maximumMessageBytes)
{
    throw new InvalidOperationException("Request exceeds IPC message limit.");
}

await using var pipe = new NamedPipeClientStream(
    ".",
    pipeName,
    PipeDirection.InOut,
    PipeOptions.Asynchronous);

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await pipe.ConnectAsync(timeout.Token);

await WriteFrameAsync(pipe, requestBytes, timeout.Token);
byte[] responseBytes = await ReadFrameAsync(pipe, timeout.Token);

using JsonDocument response = JsonDocument.Parse(responseBytes);
Console.WriteLine(JsonSerializer.Serialize(response.RootElement, new JsonSerializerOptions
{
    WriteIndented = true
}));

static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
{
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
    if (length <= 0 || length > maximumMessageBytes)
    {
        throw new InvalidDataException("Response length is outside the allowed IPC limit.");
    }

    byte[] payload = new byte[length];
    await stream.ReadExactlyAsync(payload, cancellationToken);
    return payload;
}
