using System.Security.Cryptography;
using System.Text.Json;
using SirkAgent.Policy;

namespace SirkAgent.Service.Core;

internal sealed class DeviceIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object InitializationLock = new();

    private readonly string _path;
    private readonly DpapiMachineStateProtector _protector;

    public DeviceIdentityStore(string path, DpapiMachineStateProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public DeviceIdentity LoadOrCreate(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        lock (InitializationLock)
        {
            return LoadOrCreateCore(tenantId);
        }
    }

    private DeviceIdentity LoadOrCreateCore(string tenantId)
    {
        if (!File.Exists(_path))
        {
            var created = DeviceIdentity.Create(tenantId);
            Save(created);
            return created;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            if (protectedBytes.Length == 0)
                throw new InvalidDataException("Protected device identity is empty.");

            var plaintext = _protector.Unprotect(protectedBytes);
            var identity = JsonSerializer.Deserialize<DeviceIdentity>(plaintext, JsonOptions)
                           ?? throw new InvalidDataException("Protected device identity could not be deserialized.");

            identity.Validate(tenantId);
            return identity;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            PreserveCorruptedIdentity();
            throw new InvalidDataException(
                "Device identity validation failed. Automatic regeneration is blocked to prevent device identity replacement.",
                exception);
        }
    }

    private void Save(DeviceIdentity identity)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions);
        var protectedBytes = _protector.Protect(plaintext);
        AtomicFile.Write(_path, protectedBytes);
    }

    private void PreserveCorruptedIdentity()
    {
        if (!File.Exists(_path))
            return;

        var evidencePath = Path.Combine(
            Path.GetDirectoryName(_path) ?? string.Empty,
            $"device-identity.tampered.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bin");

        File.Copy(_path, evidencePath, overwrite: false);
    }
}

internal sealed record DeviceIdentity(
    int SchemaVersion,
    string TenantId,
    string DeviceId,
    string InitialMachineName,
    DateTimeOffset CreatedAtUtc)
{
    public static DeviceIdentity Create(string tenantId) => new(
        SchemaVersion: 1,
        TenantId: tenantId,
        DeviceId: Guid.NewGuid().ToString("D"),
        InitialMachineName: Environment.MachineName,
        CreatedAtUtc: DateTimeOffset.UtcNow);

    public void Validate(string expectedTenantId)
    {
        if (SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported device identity schema version: {SchemaVersion}.");

        if (!string.Equals(TenantId, expectedTenantId, StringComparison.Ordinal))
            throw new InvalidDataException("Device identity tenant does not match the configured tenant.");

        if (!Guid.TryParseExact(DeviceId, "D", out _))
            throw new InvalidDataException("Device identity contains an invalid Device ID.");

        if (string.IsNullOrWhiteSpace(InitialMachineName))
            throw new InvalidDataException("Device identity does not contain the initial machine name.");

        if (CreatedAtUtc == default || CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("Device identity creation timestamp is invalid.");
    }
}
