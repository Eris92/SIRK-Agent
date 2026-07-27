using System.Security.Cryptography;

namespace SirkAgent.Policy;

public interface IPolicyPublicKeyProvider
{
    ECDsa? GetKey(string keyId);
}

public sealed class PolicyValidator
{
    private readonly IPolicyPublicKeyProvider _keys;

    public PolicyValidator(IPolicyPublicKeyProvider keys)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public PolicyValidationResult Validate(PolicyEnvelope policy, PolicyValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(policy.TenantId, context.TenantId, StringComparison.Ordinal))
            return PolicyValidationResult.Reject("TENANT_MISMATCH", "Policy belongs to another tenant.");

        if (!string.Equals(policy.DeviceId, context.DeviceId, StringComparison.OrdinalIgnoreCase))
            return PolicyValidationResult.Reject("DEVICE_MISMATCH", "Policy is not assigned to this device.");

        if (policy.Epoch < context.CurrentEpoch)
            return PolicyValidationResult.Reject("EPOCH_ROLLBACK", "Policy epoch is older than the accepted epoch.");

        if (policy.Epoch == context.CurrentEpoch && policy.Version <= context.CurrentVersion)
            return PolicyValidationResult.Reject("VERSION_ROLLBACK", "Policy version is not newer than the accepted version.");

        if (context.SeenNonces.Contains(policy.Nonce))
            return PolicyValidationResult.Reject("REPLAY", "Policy nonce has already been used.");

        if (policy.NotBeforeUtc > context.UtcNow + context.AllowedClockSkew)
            return PolicyValidationResult.Reject("NOT_YET_VALID", "Policy validity period has not started.");

        if (policy.ExpiresAtUtc < context.UtcNow - context.AllowedClockSkew)
            return PolicyValidationResult.Reject("EXPIRED", "Policy has expired.");

        if (policy.ExpiresAtUtc <= policy.NotBeforeUtc)
            return PolicyValidationResult.Reject("INVALID_PERIOD", "Policy expiration must be later than its start time.");

        if (policy.Mode is AgentMode.Investigation or AgentMode.InsiderRisk && string.IsNullOrWhiteSpace(policy.CaseId))
            return PolicyValidationResult.Reject("CASE_REQUIRED", "Investigation and InsiderRisk policies require a Case ID.");

        if (!string.Equals(policy.Signature.Algorithm, "ES256", StringComparison.Ordinal))
            return PolicyValidationResult.Reject("UNSUPPORTED_ALGORITHM", "Only ES256 signatures are accepted.");

        using var key = _keys.GetKey(policy.Signature.KeyId);
        if (key is null)
            return PolicyValidationResult.Reject("UNKNOWN_KEY", "The signing key is not trusted.");

        byte[] signature;
        try
        {
            signature = DecodeBase64Url(policy.Signature.Value);
        }
        catch (FormatException)
        {
            return PolicyValidationResult.Reject("INVALID_SIGNATURE_ENCODING", "Signature is not valid base64url.");
        }

        if (signature.Length != 64)
            return PolicyValidationResult.Reject("INVALID_SIGNATURE_LENGTH", "ES256 signature must contain 64 bytes in IEEE P1363 format.");

        var payload = CanonicalJson.SerializePayloadWithoutSignature(policy);
        var valid = key.VerifyData(
            payload,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return valid
            ? PolicyValidationResult.Success()
            : PolicyValidationResult.Reject("BAD_SIGNATURE", "Policy signature verification failed.");
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("Empty base64url value.");

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length.")
        };

        return Convert.FromBase64String(padded);
    }
}
