using System.Security.Cryptography;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class PolicyValidatorTests
{
    [Fact]
    public void Accepts_valid_signed_policy()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = CreatePolicy();
        policy = policy with { Signature = Sign(policy, signingKey) };

        var validator = new PolicyValidator(new StaticKeyProvider(signingKey.ExportSubjectPublicKeyInfo()));
        var result = validator.Validate(policy, CreateContext());

        Assert.True(result.IsValid, result.Message);
        Assert.Equal("OK", result.Code);
    }

    [Fact]
    public void Rejects_policy_for_another_device()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = CreatePolicy() with { DeviceId = "OTHER-PC" };
        policy = policy with { Signature = Sign(policy, signingKey) };

        var validator = new PolicyValidator(new StaticKeyProvider(signingKey.ExportSubjectPublicKeyInfo()));
        var result = validator.Validate(policy, CreateContext());

        Assert.False(result.IsValid);
        Assert.Equal("DEVICE_MISMATCH", result.Code);
    }

    [Fact]
    public void Rejects_replayed_nonce()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = CreatePolicy();
        policy = policy with { Signature = Sign(policy, signingKey) };
        var context = CreateContext() with { SeenNonces = new HashSet<string> { policy.Nonce } };

        var validator = new PolicyValidator(new StaticKeyProvider(signingKey.ExportSubjectPublicKeyInfo()));
        var result = validator.Validate(policy, context);

        Assert.False(result.IsValid);
        Assert.Equal("REPLAY", result.Code);
    }

    [Fact]
    public void Requires_case_id_for_investigation()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = CreatePolicy() with { Mode = AgentMode.Investigation, CaseId = null };
        policy = policy with { Signature = Sign(policy, signingKey) };

        var validator = new PolicyValidator(new StaticKeyProvider(signingKey.ExportSubjectPublicKeyInfo()));
        var result = validator.Validate(policy, CreateContext());

        Assert.False(result.IsValid);
        Assert.Equal("CASE_REQUIRED", result.Code);
    }

    [Fact]
    public void Requires_formal_authorization_for_investigation()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = CreatePolicy() with { Mode = AgentMode.Investigation, CaseId = "CASE-1" };
        policy = policy with { Signature = Sign(policy, signingKey) };

        var validator = new PolicyValidator(new StaticKeyProvider(signingKey.ExportSubjectPublicKeyInfo()));
        var result = validator.Validate(policy, CreateContext());

        Assert.False(result.IsValid);
        Assert.Equal("AUTHORIZATION_REQUIRED", result.Code);
    }

    [Theory]
    [InlineData(AgentMode.Investigation, null, true)]
    [InlineData(AgentMode.InsiderRisk, "HR", true)]
    [InlineData(AgentMode.InsiderRisk, "Security", true)]
    [InlineData(AgentMode.InsiderRisk, "Manager", false)]
    public void Enforces_formal_case_approval(AgentMode mode, string? trigger, bool accepted)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var policy = CreatePolicy() with
        {
            Mode = mode,
            CaseId = "CASE-2",
            Authorization = new PolicyAuthorization
            {
                ReasonCode = "SEC-REVIEW",
                ApprovedBy = ["security@example.test", "legal@example.test"],
                TargetUserSid = "S-1-5-21-1",
                RetentionDays = 90,
                TriggerSource = trigger
            }
        };
        policy = policy with { Signature = Sign(policy, signingKey) };

        var validator = new PolicyValidator(new StaticKeyProvider(signingKey.ExportSubjectPublicKeyInfo()));
        var result = validator.Validate(policy, CreateContext());

        Assert.Equal(accepted, result.IsValid);
        if (!accepted)
            Assert.Equal("TRIGGER_REQUIRED", result.Code);
    }

    private static PolicyEnvelope CreatePolicy() => new()
    {
        TenantId = "investa",
        DeviceId = "K24-085",
        PolicyId = Guid.NewGuid().ToString("D"),
        CaseId = null,
        Version = 11,
        Epoch = 2,
        NotBeforeUtc = DateTimeOffset.Parse("2026-07-27T05:00:00Z"),
        ExpiresAtUtc = DateTimeOffset.Parse("2026-07-28T05:00:00Z"),
        Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        Mode = AgentMode.Security,
        Settings = new Dictionary<string, object?>
        {
            ["tamperProtection"] = true,
            ["heartbeatSeconds"] = 30
        },
        Signature = new PolicySignature { Algorithm = "ES256", KeyId = "tenant-policy-2026-01", Value = "pending" }
    };

    private static PolicyValidationContext CreateContext() => new(
        "investa",
        "K24-085",
        CurrentEpoch: 2,
        CurrentVersion: 10,
        UtcNow: DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
        AllowedClockSkew: TimeSpan.FromMinutes(5),
        SeenNonces: new HashSet<string>());

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
