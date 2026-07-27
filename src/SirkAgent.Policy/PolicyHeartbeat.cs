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
}

public static class PolicyHeartbeatFactory
{
    public static PolicyHeartbeat Create(
        PolicyState state,
        string tenantId,
        string deviceId,
        DateTimeOffset timestampUtc,
        string stateStatus = "OK")
    {
        ArgumentNullException.ThrowIfNull(state);

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
            StateStatus = stateStatus
        };
    }
}
