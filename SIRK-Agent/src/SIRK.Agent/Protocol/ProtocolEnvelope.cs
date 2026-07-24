using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sirk.Agent.Protocol;

internal sealed record ProtocolEnvelope
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("messageType")]
    public string MessageType { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("operatorId")]
    public string OperatorId { get; init; } = string.Empty;

    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

internal sealed record ProtocolResponse(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] object? Result,
    [property: JsonPropertyName("error")] ProtocolError? Error);

internal sealed record ProtocolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
