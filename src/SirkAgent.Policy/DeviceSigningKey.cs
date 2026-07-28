using System.Security.Cryptography;
using System.Text;

namespace SirkAgent.Policy;

public static class DeviceSigningKey
{
    private static readonly CngProvider Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    public static string NameFor(string tenantId, string deviceId)
    {
        var material = Encoding.UTF8.GetBytes(tenantId + "\n" + deviceId);
        return "SIRK-Agent-Portal-" + Convert.ToHexString(SHA256.HashData(material))[..32];
    }

    public static byte[] Create(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        var parameters = new CngKeyCreationParameters
        {
            Provider = Provider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            UIPolicy = new CngUIPolicy(CngUIProtectionLevels.None)
        };
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, parameters);
        using var signer = new ECDsaCng(key);
        return signer.ExportSubjectPublicKeyInfo();
    }

    public static bool Exists(string keyName) =>
        CngKey.Exists(keyName, Provider, CngKeyOpenOptions.MachineKey);

    public static byte[] Sign(string keyName, ReadOnlySpan<byte> data)
    {
        using var key = CngKey.Open(keyName, Provider, CngKeyOpenOptions.MachineKey);
        using var signer = new ECDsaCng(key);
        return signer.SignData(data, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static void Delete(string keyName)
    {
        using var key = CngKey.Open(keyName, Provider, CngKeyOpenOptions.MachineKey);
        key.Delete();
    }
}
