using System.Security.Cryptography;
using System.Text.Json;
using SirkAgent.Policy;

const string tenantId = "investa";
var deviceId = Environment.MachineName;
var now = DateTimeOffset.UtcNow;
var statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent", "policy-state.bin");

Console.WriteLine("SIRK Agent Policy Test Harness");
Console.WriteLine($"Device: {deviceId}");
Console.WriteLine($"State:  {statePath}");

using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var publicKey = signingKey.ExportSubjectPublicKeyInfo();
var keyProvider = new StaticKeyProvider(publicKey);
var store = new FilePolicyStateStore(statePath, new DpapiMachineStateProtector());
var service = new PolicyAcceptanceService(new PolicyValidator(keyProvider), store);

var current = store.Load();
var policy = new PolicyEnvelope
{
    TenantId = tenantId,
    DeviceId = deviceId,
    PolicyId = Guid.NewGuid().ToString("D"),
    CaseId = "TEST-" + now.ToString("yyyyMMdd-HHmmss"),
    Version = current.Version + 1,
    Epoch = Math.Max(1, current.Epoch),
    NotBeforeUtc = now.AddMinutes(-1),
    ExpiresAtUtc = now.AddHours(1),
    Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
    Mode = AgentMode.Investigation,
    Settings = new Dictionary<string, object?>
    {
        ["tamperProtection"] = true,
        ["heartbeatSeconds"] = 30,
        ["screenshotsOnEvent"] = false
    },
    Signature = new PolicySignature { Algorithm = "ES256", KeyId = "test-key", Value = "pending" }
};

var signature = signingKey.SignData(
    CanonicalJson.SerializePayloadWithoutSignature(policy),
    HashAlgorithmName.SHA256,
    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
policy = policy with
{
    Signature = policy.Signature with
    {
        Value = Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    }
};

var result = service.ValidateAndAccept(policy, tenantId, deviceId, now, TimeSpan.FromMinutes(5));
Console.WriteLine($"Policy result: {result.Validation.Code} - {result.Validation.Message}");
if (!result.IsAccepted)
    return 2;

var checker = new PolicyStateHealthChecker(statePath, store);
var healthy = checker.Check();
Console.WriteLine($"State health: {healthy.Code} - {healthy.Message}");
if (!healthy.IsHealthy)
    return 3;

var heartbeat = PolicyHeartbeatFactory.Create(
    result.State,
    tenantId,
    deviceId,
    DateTimeOffset.UtcNow,
    healthy.Code);
Console.WriteLine(JsonSerializer.Serialize(heartbeat, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

var replay = service.ValidateAndAccept(policy, tenantId, deviceId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
Console.WriteLine($"Replay test: {replay.Validation.Code}");
if (replay.Validation.Code != "VERSION_ROLLBACK" && replay.Validation.Code != "REPLAY")
    return 4;

var originalState = File.ReadAllBytes(statePath);
try
{
    var tampered = originalState.ToArray();
    tampered[tampered.Length / 2] ^= 0x5A;
    File.WriteAllBytes(statePath, tampered);

    var tamperResult = checker.Check();
    Console.WriteLine($"Tamper test: {tamperResult.Code} - {tamperResult.Message}");
    if (tamperResult.IsHealthy)
        return 5;
}
finally
{
    File.WriteAllBytes(statePath, originalState);
}

var restored = checker.Check();
Console.WriteLine($"Restore test: {restored.Code}");
if (!restored.IsHealthy)
    return 6;

Console.WriteLine("TEST PASSED");
return 0;

sealed class StaticKeyProvider(byte[] subjectPublicKeyInfo) : IPolicyPublicKeyProvider
{
    public ECDsa? GetKey(string keyId)
    {
        if (!string.Equals(keyId, "test-key", StringComparison.Ordinal))
            return null;

        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return key;
    }
}
