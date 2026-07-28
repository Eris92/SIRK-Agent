using System.Security.Cryptography;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class PolicyStateStoreTests
{
    [Fact]
    public void Protected_file_store_round_trips_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sirk-policy-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.bin");

        try
        {
            var store = new FilePolicyStateStore(path, new TestProtector());
            var expected = new PolicyState
            {
                Epoch = 4,
                Version = 18,
                ActivePolicyHash = "ABC123",
                ActivePolicyId = "policy-18",
                ActiveCaseId = "INC-2026-01",
                AcceptedAtUtc = DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
                SeenNonces = new[] { "nonce-1", "nonce-2" }
            };

            store.Save(expected);
            var actual = store.Load();

            Assert.Equal(expected.Epoch, actual.Epoch);
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.ActivePolicyHash, actual.ActivePolicyHash);
            Assert.Equal(expected.ActivePolicyId, actual.ActivePolicyId);
            Assert.Equal(expected.ActiveCaseId, actual.ActiveCaseId);
            Assert.Equal(expected.AcceptedAtUtc, actual.AcceptedAtUtc);
            Assert.Equal(expected.SeenNonces, actual.SeenNonces);
            Assert.DoesNotContain("policy-18", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_initialization_atomically_leaves_a_valid_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sirk-policy-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.bin");

        try
        {
            var store = new FilePolicyStateStore(path, new TestProtector());
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 32).Select(version => Task.Run(() =>
            {
                start.Wait();
                store.Save(PolicyState.Empty with { Version = version });
            })).ToArray();

            start.Set();
            await Task.WhenAll(tasks);

            var state = store.Load();
            Assert.InRange(state.Version, 0, 31);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), file => file.Contains(".tmp.", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Acceptance_persists_nonce_and_rejects_replay()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stateStore = new MemoryStateStore();
        var validator = new PolicyValidator(new StaticKeyProvider(signingKey.ExportSubjectPublicKeyInfo()));
        var service = new PolicyAcceptanceService(validator, stateStore);
        var policy = CreatePolicy();
        policy = policy with { Signature = Sign(policy, signingKey) };
        var now = DateTimeOffset.Parse("2026-07-27T08:00:00Z");

        var first = service.ValidateAndAccept(policy, "investa", "K24-085", now, TimeSpan.FromMinutes(5));
        var second = service.ValidateAndAccept(
            policy with { Version = policy.Version + 1 },
            "investa",
            "K24-085",
            now,
            TimeSpan.FromMinutes(5));

        Assert.True(first.IsAccepted, first.Validation.Message);
        Assert.False(second.IsAccepted);
        Assert.Equal("REPLAY", second.Validation.Code);
        Assert.Contains(policy.Nonce, stateStore.State.SeenNonces);
        Assert.NotNull(stateStore.State.ActivePolicyHash);
        Assert.Equal(64, stateStore.State.ActivePolicyHash!.Length);
    }

    private static PolicyEnvelope CreatePolicy() => new()
    {
        TenantId = "investa",
        DeviceId = "K24-085",
        PolicyId = "policy-12",
        CaseId = null,
        Version = 12,
        Epoch = 2,
        NotBeforeUtc = DateTimeOffset.Parse("2026-07-27T05:00:00Z"),
        ExpiresAtUtc = DateTimeOffset.Parse("2026-07-28T05:00:00Z"),
        Nonce = "nonce-unique-12",
        Mode = AgentMode.Security,
        Settings = new Dictionary<string, object?> { ["tamperProtection"] = true },
        Signature = new PolicySignature { Algorithm = "ES256", KeyId = "tenant-policy-2026-01", Value = "pending" }
    };

    private static PolicySignature Sign(PolicyEnvelope policy, ECDsa key)
    {
        var signature = key.SignData(
            CanonicalJson.SerializePayloadWithoutSignature(policy),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new PolicySignature
        {
            Algorithm = "ES256",
            KeyId = "tenant-policy-2026-01",
            Value = Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        };
    }

    private sealed class MemoryStateStore : IPolicyStateStore
    {
        public PolicyState State { get; private set; } = PolicyState.Empty;
        public PolicyState Load() => State;
        public void Save(PolicyState state) => State = state;
    }

    private sealed class TestProtector : IStateProtector
    {
        private const byte Mask = 0xA5;

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext);
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Transform(protectedData);

        private static byte[] Transform(ReadOnlySpan<byte> source)
        {
            var result = source.ToArray();
            for (var index = 0; index < result.Length; index++)
                result[index] ^= Mask;
            return result;
        }
    }

    private sealed class StaticKeyProvider : IPolicyPublicKeyProvider
    {
        private readonly byte[] _subjectPublicKeyInfo;
        public StaticKeyProvider(byte[] subjectPublicKeyInfo) => _subjectPublicKeyInfo = subjectPublicKeyInfo;

        public ECDsa? GetKey(string keyId)
        {
            if (!string.Equals(keyId, "tenant-policy-2026-01", StringComparison.Ordinal))
                return null;

            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);
            return key;
        }
    }
}
