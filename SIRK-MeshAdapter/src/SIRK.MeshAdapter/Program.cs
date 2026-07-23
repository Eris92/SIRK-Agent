using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const string PipeName = "SIRK.Agent.v1";
const int MaximumMessageBytes = 64 * 1024;
const int MaximumInputCharacters = 32 * 1024;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = false,
    WriteIndented = false
};

try
{
    string inputJson = await ReadLimitedInputAsync(Console.In, MaximumInputCharacters);
    AdapterRequest? input = JsonSerializer.Deserialize<AdapterRequest>(inputJson, jsonOptions);

    if (input is null)
    {
        return await WriteFailureAsync("empty_request", "Adapter request is empty.", 2, jsonOptions);
    }

    string? validationError = Validate(input);
    if (validationError is not null)
    {
        return await WriteFailureAsync("invalid_request", validationError, 2, jsonOptions);
    }

    DateTimeOffset now = DateTimeOffset.UtcNow;
    var envelope = new
    {
        protocolVersion = 1,
        messageType = input.MessageType,
        requestId = Guid.NewGuid().ToString("D"),
        deviceId = input.DeviceId,
        operatorId = input.OperatorId,
        issuedAt = now,
        expiresAt = now.AddSeconds(30),
        nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
        payload = input.Payload.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { }, jsonOptions)
            : input.Payload
    };

    byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, jsonOptions);
    if (requestBytes.Length > MaximumMessageBytes)
    {
        return await WriteFailureAsync("request_too_large", "SIRK Protocol request exceeds the IPC limit.", 2, jsonOptions);
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await using var pipe = new NamedPipeClientStream(
        ".",
        PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous);

    await pipe.ConnectAsync(timeout.Token);
    await WriteFrameAsync(pipe, requestBytes, timeout.Token);
    byte[] responseBytes = await ReadFrameAsync(pipe, timeout.Token);

    string responseJson = Encoding.UTF8.GetString(responseBytes);
    await Console.Out.WriteLineAsync(responseJson);
    return 0;
}
catch (JsonException)
{
    return await WriteFailureAsync("invalid_json", "Input is not valid JSON.", 2, jsonOptions);
}
catch (OperationCanceledException)
{
    return await WriteFailureAsync("agent_timeout", "SIRK-Agent did not respond within the allowed time.", 3, jsonOptions);
}
catch (IOException exception)
{
    Console.Error.WriteLine($"SIRK-MeshAdapter IPC error: {exception.GetType().Name}");
    return await WriteFailureAsync("agent_unavailable", "SIRK-Agent IPC is unavailable.", 3, jsonOptions);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"SIRK-MeshAdapter unexpected error: {exception.GetType().Name}");
    return await WriteFailureAsync("adapter_error", "SIRK-MeshAdapter failed safely.", 4, jsonOptions);
}

static string? Validate(AdapterRequest input)
{
    string[] allowedMessages =
    {
        "System.Ping",
        "System.GetStatus",
        "System.GetCapabilities",
        "Workspace.GetCapabilities",
        "Workspace.CaptureFrame"
    };

    if (!allowedMessages.Contains(input.MessageType, StringComparer.Ordinal))
    {
        return "messageType is not enabled by SIRK-MeshAdapter.";
    }

    if (string.IsNullOrWhiteSpace(input.DeviceId) || input.DeviceId.Length > 256)
    {
        return "deviceId is required and limited to 256 characters.";
    }

    if (string.IsNullOrWhiteSpace(input.OperatorId) || input.OperatorId.Length > 256)
    {
        return "operatorId is required and limited to 256 characters.";
    }

    return null;
}

static async Task<string> ReadLimitedInputAsync(TextReader reader, int maximumCharacters)
{
    char[] buffer = new char[4096];
    var builder = new StringBuilder();

    while (true)
    {
        int read = await reader.ReadAsync(buffer.AsMemory());
        if (read == 0)
        {
            break;
        }

        if (builder.Length + read > maximumCharacters)
        {
            throw new InvalidDataException("Adapter input exceeds the allowed limit.");
        }

        builder.Append(buffer, 0, read);
    }

    return builder.ToString();
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
    if (length <= 0 || length > MaximumMessageBytes)
    {
        throw new InvalidDataException("Agent response length is outside the allowed IPC limit.");
    }

    byte[] payload = new byte[length];
    await stream.ReadExactlyAsync(payload, cancellationToken);
    return payload;
}

static async Task<int> WriteFailureAsync(
    string code,
    string message,
    int exitCode,
    JsonSerializerOptions jsonOptions)
{
    string json = JsonSerializer.Serialize(new AdapterFailure(false, new AdapterError(code, message)), jsonOptions);
    await Console.Out.WriteLineAsync(json);
    return exitCode;
}

internal sealed record AdapterRequest
{
    [JsonPropertyName("messageType")]
    public string MessageType { get; init; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("operatorId")]
    public string OperatorId { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

internal sealed record AdapterFailure(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] AdapterError Error);

internal sealed record AdapterError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
