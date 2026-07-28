using System.Security.Cryptography;
using SirkAgent.Policy;
using Xunit;

namespace SirkAgent.Policy.Tests;

public sealed class UpdatePackageVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sirk-update-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void VerifyAcceptsSignedPackageAndRejectsTamper()
    {
        Directory.CreateDirectory(_root);
        var names = new[] { "SirkAgent.Service.exe", "SirkAgent.Service.dll", "SirkAgent.Policy.dll", "sirkctl.exe" };
        foreach (var name in names) File.WriteAllText(Path.Combine(_root, name), "test-" + name);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var unsigned = new UpdateManifest(1, "SIRK Agent", "1.0.0", "win-x64",
            names.Select(name => new UpdateManifestFile(name,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, name)))))).ToArray(),
            new PolicySignature { Algorithm = "ES256", KeyId = "release", Value = "pending" });
        var signature = key.SignData(CanonicalJson.SerializeWithoutTopLevelSignature(unsigned),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var manifest = unsigned with { Signature = unsigned.Signature with { Value = Base64Url(signature) } };
        var verifier = new UpdatePackageVerifier(new TestKeyProvider(key.ExportSubjectPublicKeyInfo()));

        var accepted = verifier.Verify(_root, manifest);
        Assert.True(accepted.Accepted, $"{accepted.Code}: {accepted.Message}");
        Assert.Equal("UPDATE_VERSION_ROLLBACK", verifier.Verify(_root, manifest, "1.0.1").Code);
        File.AppendAllText(Path.Combine(_root, names[0]), "tamper");
        Assert.Equal("UPDATE_FILE_HASH_MISMATCH", verifier.Verify(_root, manifest).Code);
    }

    [Fact]
    public void VerifyRejectsTraversalAndUnknownKey()
    {
        Directory.CreateDirectory(_root);
        var manifest = new UpdateManifest(1, "SIRK Agent", "1.0.0", "win-x64",
            [new UpdateManifestFile("../outside.exe", new string('A', 64))],
            new PolicySignature { Algorithm = "ES256", KeyId = "unknown", Value = "AA" });
        var result = new UpdatePackageVerifier(new EmptyKeyProvider()).Verify(_root, manifest);
        Assert.Equal("UPDATE_KEY_UNKNOWN", result.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TestKeyProvider(byte[] publicKey) : IPolicyPublicKeyProvider
    {
        public ECDsa? GetKey(string keyId)
        {
            if (keyId != "release") return null;
            var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key;
        }
    }

    private sealed class EmptyKeyProvider : IPolicyPublicKeyProvider
    {
        public ECDsa? GetKey(string keyId) => null;
    }
}
