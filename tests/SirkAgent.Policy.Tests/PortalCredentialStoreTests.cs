using System.Text;
using SirkAgent.Policy;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class PortalCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirk-portal-credential-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveProtectsTokenAndLoadRoundTrips()
    {
        var path = Path.Combine(_root, "portal-credential.bin");
        var store = new PortalCredentialStore(path, new TestProtector());
        var expected = new PortalCredential(2, "investa", Guid.NewGuid().ToString(),
            "https://portal.example/api/agent/v1/checkin", "secret-device-token", DateTimeOffset.UtcNow,
            "private-key-material");

        store.Save(expected);

        Assert.Equal(expected, store.Load());
        Assert.DoesNotContain("secret-device-token", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-material", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaThreeRequiresKeyName()
    {
        var path = Path.Combine(_root, "portal-credential.bin");
        var store = new PortalCredentialStore(path, new TestProtector());
        store.Save(new PortalCredential(3, "investa", "device",
            "https://portal.example/api/agent/v1/checkin", "token", DateTimeOffset.UtcNow));

        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class TestProtector : IStateProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
            Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) =>
            Convert.FromBase64String(Encoding.UTF8.GetString(protectedData));
    }
}
