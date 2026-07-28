using System.Text.Json;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class PortalPolicyDeliveryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirk-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void StoreAcceptsMatchingSignedPolicyAndUsesHashedFileName()
    {
        var store = new PortalPolicyDeliveryStore(_root, _json);
        var raw = JsonSerializer.SerializeToElement(CreatePolicy("investa", "device-1", "../policy-1"), _json);

        Assert.Equal(1, store.Store("investa", "device-1", [raw]));
        var file = Assert.Single(Directory.GetFiles(_root, "*.policy.json"));
        Assert.DoesNotContain("policy-1", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.Equal(0, store.Store("investa", "device-1", [raw]));
    }

    [Fact]
    public void StoreRejectsCrossDeviceAndUnsignedPolicies()
    {
        var store = new PortalPolicyDeliveryStore(_root, _json);
        var crossDevice = JsonSerializer.SerializeToElement(CreatePolicy("investa", "device-2", "policy-2"), _json);
        var unsigned = JsonSerializer.SerializeToElement(CreatePolicy("investa", "device-1", "policy-3") with
        {
            Signature = new PolicySignature { Algorithm = "ES256", KeyId = "key", Value = "" }
        }, _json);

        Assert.Equal(0, store.Store("investa", "device-1", [crossDevice, unsigned]));
        Assert.Empty(Directory.GetFiles(_root));
    }

    private static PolicyEnvelope CreatePolicy(string tenantId, string deviceId, string policyId) => new()
    {
        TenantId = tenantId,
        DeviceId = deviceId,
        PolicyId = policyId,
        Version = 1,
        Epoch = 1,
        NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        Nonce = Guid.NewGuid().ToString("N"),
        Mode = AgentMode.Normal,
        Settings = new Dictionary<string, object?>(),
        Signature = new PolicySignature { Algorithm = "ES256", KeyId = "key", Value = "signature" }
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
