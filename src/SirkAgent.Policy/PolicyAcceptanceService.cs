using System.Security.Cryptography;

namespace SirkAgent.Policy;

public sealed record PolicyAcceptanceResult(
    bool IsAccepted,
    PolicyValidationResult Validation,
    PolicyState State);

public sealed class PolicyAcceptanceService
{
    private const int MaximumRememberedNonces = 4096;

    private readonly PolicyValidator _validator;
    private readonly IPolicyStateStore _stateStore;
    private readonly object _sync = new();

    public PolicyAcceptanceService(PolicyValidator validator, IPolicyStateStore stateStore)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public PolicyAcceptanceResult ValidateAndAccept(
        PolicyEnvelope policy,
        string tenantId,
        string deviceId,
        DateTimeOffset utcNow,
        TimeSpan allowedClockSkew)
    {
        ArgumentNullException.ThrowIfNull(policy);

        lock (_sync)
        {
            var current = _stateStore.Load();
            var seenNonces = current.SeenNonces.ToHashSet(StringComparer.Ordinal);
            var context = new PolicyValidationContext(
                tenantId,
                deviceId,
                current.Epoch,
                current.Version,
                utcNow,
                allowedClockSkew,
                seenNonces);

            var validation = _validator.Validate(policy, context);
            if (!validation.IsValid)
                return new PolicyAcceptanceResult(false, validation, current);

            var canonicalPayload = CanonicalJson.SerializePayloadWithoutSignature(policy);
            var policyHash = Convert.ToHexString(SHA256.HashData(canonicalPayload));

            var updatedNonces = current.SeenNonces
                .Append(policy.Nonce)
                .Distinct(StringComparer.Ordinal)
                .TakeLast(MaximumRememberedNonces)
                .ToArray();

            var updated = new PolicyState
            {
                Epoch = policy.Epoch,
                Version = policy.Version,
                ActivePolicyHash = policyHash,
                ActivePolicyId = policy.PolicyId,
                ActiveCaseId = policy.CaseId,
                AcceptedAtUtc = utcNow,
                SeenNonces = updatedNonces
            };

            _stateStore.Save(updated);
            return new PolicyAcceptanceResult(true, validation, updated);
        }
    }
}
