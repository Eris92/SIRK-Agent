using System.Text.Json.Serialization;

namespace SirkAgent.Policy;

public sealed record PolicyState
{
    [JsonPropertyName("epoch")]
    public long Epoch { get; init; }

    [JsonPropertyName("version")]
    public long Version { get; init; }

    [JsonPropertyName("activePolicyHash")]
    public string? ActivePolicyHash { get; init; }

    [JsonPropertyName("activePolicyId")]
    public string? ActivePolicyId { get; init; }

    [JsonPropertyName("activeCaseId")]
    public string? ActiveCaseId { get; init; }

    [JsonPropertyName("acceptedAtUtc")]
    public DateTimeOffset? AcceptedAtUtc { get; init; }

    [JsonPropertyName("seenNonces")]
    public IReadOnlyList<string> SeenNonces { get; init; } = Array.Empty<string>();

    public static PolicyState Empty { get; } = new();
}

public interface IPolicyStateStore
{
    PolicyState Load();
    void Save(PolicyState state);
}

public interface IStateProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}
