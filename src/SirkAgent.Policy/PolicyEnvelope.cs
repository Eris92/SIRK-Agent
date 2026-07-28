using System.Text.Json.Serialization;

namespace SirkAgent.Policy;

public enum AgentMode
{
    Normal,
    Security,
    Investigation,
    InsiderRisk,
    Emergency
}

public sealed record PolicyEnvelope
{
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("policyId")]
    public required string PolicyId { get; init; }

    [JsonPropertyName("caseId")]
    public string? CaseId { get; init; }

    [JsonPropertyName("authorization")]
    public PolicyAuthorization? Authorization { get; init; }

    [JsonPropertyName("version")]
    public required long Version { get; init; }

    [JsonPropertyName("epoch")]
    public required long Epoch { get; init; }

    [JsonPropertyName("notBeforeUtc")]
    public required DateTimeOffset NotBeforeUtc { get; init; }

    [JsonPropertyName("expiresAtUtc")]
    public required DateTimeOffset ExpiresAtUtc { get; init; }

    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }

    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required AgentMode Mode { get; init; }

    [JsonPropertyName("settings")]
    public required Dictionary<string, object?> Settings { get; init; }

    [JsonPropertyName("signature")]
    public required PolicySignature Signature { get; init; }
}

public sealed record PolicyAuthorization
{
    [JsonPropertyName("reasonCode")]
    public required string ReasonCode { get; init; }

    [JsonPropertyName("approvedBy")]
    public required string[] ApprovedBy { get; init; }

    [JsonPropertyName("targetUserSid")]
    public string? TargetUserSid { get; init; }

    [JsonPropertyName("targetSessionId")]
    public int? TargetSessionId { get; init; }

    [JsonPropertyName("retentionDays")]
    public required int RetentionDays { get; init; }

    [JsonPropertyName("triggerSource")]
    public string? TriggerSource { get; init; }
}

public sealed record PolicySignature
{
    [JsonPropertyName("algorithm")]
    public required string Algorithm { get; init; }

    [JsonPropertyName("keyId")]
    public required string KeyId { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed record PolicyValidationContext(
    string TenantId,
    string DeviceId,
    long CurrentEpoch,
    long CurrentVersion,
    DateTimeOffset UtcNow,
    TimeSpan AllowedClockSkew,
    IReadOnlySet<string> SeenNonces);

public sealed record PolicyValidationResult(bool IsValid, string Code, string Message)
{
    public static PolicyValidationResult Success() => new(true, "OK", "Policy accepted.");
    public static PolicyValidationResult Reject(string code, string message) => new(false, code, message);
}
