using System.Security.Cryptography;
using System.Text.Json;

namespace SirkAgent.Policy;

public sealed record TrustedKeyDocument(IReadOnlyList<TrustedKeyEntry> Keys);
public sealed record TrustedKeyEntry(string KeyId, string PublicKeyPem);

public sealed class PemPolicyPublicKeyProvider : IPolicyPublicKeyProvider
{
    private readonly IReadOnlyDictionary<string, string> _keys;

    private PemPolicyPublicKeyProvider(IReadOnlyDictionary<string, string> keys) => _keys = keys;

    public static PemPolicyPublicKeyProvider Load(string path, JsonSerializerOptions? options = null)
    {
        if (!File.Exists(path))
            return new PemPolicyPublicKeyProvider(new Dictionary<string, string>());
        var document = JsonSerializer.Deserialize<TrustedKeyDocument>(
                           File.ReadAllBytes(path), options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? new TrustedKeyDocument([]);
        return new PemPolicyPublicKeyProvider(document.Keys
            .Where(entry => !string.IsNullOrWhiteSpace(entry.KeyId) && !string.IsNullOrWhiteSpace(entry.PublicKeyPem))
            .ToDictionary(entry => entry.KeyId, entry => entry.PublicKeyPem, StringComparer.Ordinal));
    }

    public ECDsa? GetKey(string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var pem)) return null;
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }
}
