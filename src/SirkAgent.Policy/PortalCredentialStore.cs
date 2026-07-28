using System.Text.Json;

namespace SirkAgent.Policy;

public sealed record PortalCredential(
    int SchemaVersion,
    string TenantId,
    string DeviceId,
    string Endpoint,
    string DeviceToken,
    DateTimeOffset EnrolledAtUtc,
    string? PrivateKeyPkcs8 = null,
    string? KeyName = null);

public sealed class PortalCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object Sync = new();
    private readonly string _path;
    private readonly IStateProtector _protector;

    public PortalCredentialStore(string path, IStateProtector protector)
    {
        _path = Path.GetFullPath(path);
        _protector = protector;
    }

    public PortalCredential? Load()
    {
        lock (Sync)
        {
            if (!File.Exists(_path)) return null;
            var plaintext = _protector.Unprotect(File.ReadAllBytes(_path));
            var value = JsonSerializer.Deserialize<PortalCredential>(plaintext, JsonOptions)
                        ?? throw new InvalidDataException("Portal credential is invalid.");
            if (value.SchemaVersion is < 1 or > 3 || string.IsNullOrWhiteSpace(value.TenantId) ||
                string.IsNullOrWhiteSpace(value.DeviceId) || string.IsNullOrWhiteSpace(value.Endpoint) ||
                string.IsNullOrWhiteSpace(value.DeviceToken) ||
                value.SchemaVersion == 2 && string.IsNullOrWhiteSpace(value.PrivateKeyPkcs8) ||
                value.SchemaVersion == 3 && string.IsNullOrWhiteSpace(value.KeyName))
                throw new InvalidDataException("Portal credential fields are invalid.");
            return value;
        }
    }

    public void Save(PortalCredential value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var protectedBytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
            var temporary = _path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, protectedBytes);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
