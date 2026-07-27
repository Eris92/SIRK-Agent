using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class PolicyStateHealthTests
{
    [Fact]
    public void Reports_missing_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sirk-health-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.bin");
        var store = new FilePolicyStateStore(path, new TestProtector());
        var checker = new PolicyStateHealthChecker(path, store);

        var result = checker.Check();

        Assert.False(result.IsHealthy);
        Assert.Equal(PolicyStateHealthStatus.Missing, result.Status);
        Assert.Equal("STATE_MISSING", result.Code);
    }

    [Fact]
    public void Reports_corrupted_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sirk-health-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.bin");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
            var store = new FilePolicyStateStore(path, new ThrowingProtector());
            var checker = new PolicyStateHealthChecker(path, store);

            var result = checker.Check();

            Assert.False(result.IsHealthy);
            Assert.Equal(PolicyStateHealthStatus.ProtectionError, result.Status);
            Assert.Equal("STATE_UNPROTECT_FAILED", result.Code);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestProtector : IStateProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protectedData.ToArray();
    }

    private sealed class ThrowingProtector : IStateProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => throw new System.Security.Cryptography.CryptographicException("Tampered state.");
    }
}
