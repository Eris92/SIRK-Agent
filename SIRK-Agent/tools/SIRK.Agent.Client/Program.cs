using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

const string pipeName = "SIRK.Agent.v1";
const int maximumRequestBytes = 64 * 1024;
const int maximumResponseBytes = 16 * 1024 * 1024;

string messageType = args.Length > 0 ? args[0] : "System.Ping";
string deviceId = args.Length > 1 ? args[1] : Environment.MachineName;
string operatorId = args.Length > 2 ? args[2] : $"local:{Environment.UserName}";
JsonElement payload = ParsePayload(args.Length > 3 ? args[3] : null);

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
    payload
};

byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
if (requestBytes.Length > maximumRequestBytes)
{
    throw new InvalidOperationException("Request exceeds IPC message limit.");
}

await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await pipe.ConnectAsync(timeout.Token);
await WriteFrameAsync(pipe, requestBytes, timeout.Token);
byte[] responseBytes = await ReadFrameAsync(pipe, timeout.Token);

using JsonDocument response = JsonDocument.Parse(responseBytes);
Console.WriteLine(JsonSerializer.Serialize(response.RootElement, new JsonSerializerOptions { WriteIndented = true }));

static JsonElement ParsePayload(string? argument)
{
    if (string.IsNullOrWhiteSpace(argument))
    {
        return JsonSerializer.SerializeToElement(new { });
    }

    string json = argument.StartsWith('@')
        ? File.ReadAllText(argument[1..])
        : argument;

    using JsonDocument document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidDataException("Payload must be a JSON object.");
    }

    return document.RootElement.Clone();
}

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
    if (length <= 0 || length > maximumResponseBytes)
    {
        throw new InvalidDataException("Response length is outside the allowed IPC limit.");
    }

    byte[] payload = new byte[length];
    await stream.ReadExactlyAsync(payload, cancellationToken);
    return payload;
}