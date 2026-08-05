from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / 'src/SirkAgent.Cli/Program.cs'
text = path.read_text(encoding='utf-8-sig')

text = text.replace('/api/agent/v1/rotate-key', '/api/v1/agent/rotate-key')

old_set = '''    var checkInEndpoint = endpoint.AbsolutePath.EndsWith("/api/agent/v1/checkin", StringComparison.OrdinalIgnoreCase)
        ? endpoint
        : new Uri(endpoint, "/api/agent/v1/checkin");
'''
new_set = '''    var checkInEndpoint = new UriBuilder(endpoint)
    {
        Path = "/api/v1/agent/checkin",
        Query = string.Empty
    }.Uri;
'''
if text.count(old_set) != 1:
    raise RuntimeError(f'set-portal-endpoint block: expected one occurrence, found {text.count(old_set)}')
text = text.replace(old_set, new_set, 1)

old_key = '''    var keyName = DeviceSigningKey.NameFor(credential.TenantId, credential.DeviceId);
    if (DeviceSigningKey.Exists(keyName))
        throw new InvalidOperationException("The non-exportable replacement key already exists; no state was changed.");
'''
new_key = '''    var previousKeyName = credential.KeyName
                          ?? DeviceSigningKey.NameFor(credential.TenantId, credential.DeviceId);
    var keyName = previousKeyName + "-R" + Guid.NewGuid().ToString("N");
    if (DeviceSigningKey.Exists(keyName))
        throw new InvalidOperationException("The non-exportable replacement key already exists; no state was changed.");
'''
if text.count(old_key) != 1:
    raise RuntimeError(f'rotate key name block: expected one occurrence, found {text.count(old_key)}')
text = text.replace(old_key, new_key, 1)

old_save = '''        store.Save(credential with
        {
            SchemaVersion = 3,
            PrivateKeyPkcs8 = null,
            KeyName = keyName
        });
'''
new_save = '''        store.Save(credential with
        {
            SchemaVersion = 3,
            PrivateKeyPkcs8 = null,
            KeyName = keyName
        });
        if (!string.Equals(previousKeyName, keyName, StringComparison.Ordinal) &&
            DeviceSigningKey.Exists(previousKeyName))
        {
            DeviceSigningKey.Delete(previousKeyName);
        }
'''
if text.count(old_save) != 1:
    raise RuntimeError(f'rotate credential save block: expected one occurrence, found {text.count(old_save)}')
text = text.replace(old_save, new_save, 1)

path.write_text(text, encoding='utf-8', newline='\n')
print('Remaining CLI management routes and device key rotation are canonical.')
