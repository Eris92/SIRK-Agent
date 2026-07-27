using System.Text.Json.Serialization;

namespace SirkAgent.Policy;

public sealed record PolicyHeartbeat
{
    [JsonPropertyName("timestampUtc")]
    public required DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }

    [JsonPropertyName("policyEpoch")]
    public required long PolicyEpoch { get; init; }

    [JsonPropertyName("policyVersion")]
    public required long PolicyVersion { get; init; }

    [JsonPropertyName("activePolicyId")]
    public string? ActivePolicyId { get; init; }

    [JsonPropertyName("activePolicyHash")]
    public string? ActivePolicyHash { get; init; }

    [JsonPropertyName("activeCaseId")]
    public string? ActiveCaseId { get; init; }

    [JsonPropertyName("stateStatus")]
    public required string StateStatus { get; init; }

    [JsonPropertyName("tamperDetected")]
    public required bool TamperDetected { get; init; }

    [JsonPropertyName("tamperReason")]
    public string? TamperReason { get; init; }

    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    [JsonPropertyName("quarantineActive")]
    public required bool QuarantineActive { get; init; }

    [JsonPropertyName("quarantineSinceUtc")]
    public DateTimeOffset? QuarantineSinceUtc { get; init; }

    [JsonPropertyName("quarantineReason")]
    public string? QuarantineReason { get; init; }
}

public static class PolicyHeartbeatFactory
{
    public static PolicyHeartbeat Create(
        PolicyState state,
        string tenantId,
        string deviceId,
        DateTimeOffset timestampUtc,
        string stateStatus = "OK",
        string trigger = "Interval",
        bool quarantineActive = false,
        DateTimeOffset? quarantineSinceUtc = null,
        string? quarantineReason = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var tamperDetected = !string.Equals(stateStatus, "OK", StringComparison.Ordinal);

        return new PolicyHeartbeat
        {
            TimestampUtc = timestampUtc,
            TenantId = tenantId,
            DeviceId = deviceId,
            PolicyEpoch = state.Epoch,
            PolicyVersion = state.Version,
            ActivePolicyId = state.ActivePolicyId,
            ActivePolicyHash = state.ActivePolicyHash,
            ActiveCaseId = state.ActiveCaseId,
            StateStatus = stateStatus,
            TamperDetected = tamperDetected,
            TamperReason = tamperDetected ? stateStatus : null,
            Trigger = trigger,
            QuarantineActive = quarantineActive,
            QuarantineSinceUtc = quarantineSinceUtc,
            QuarantineReason = quarantineReason
        };
    }
}
