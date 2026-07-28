using System.Collections.Concurrent;
using System.Text.Json;

namespace SirkAgent.Policy;

public sealed class FilePolicyStateStore : IPolicyStateStore
{
    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _path;
    private readonly IStateProtector _protector;

    public FilePolicyStateStore(string path, IStateProtector protector)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("State path is required.", nameof(path));

        _path = Path.GetFullPath(path);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public PolicyState Load()
    {
        if (!File.Exists(_path))
            return PolicyState.Empty;

        var encrypted = File.ReadAllBytes(_path);
        if (encrypted.Length == 0)
            throw new InvalidDataException("Policy state file is empty.");

        var plaintext = _protector.Unprotect(encrypted);
        var state = JsonSerializer.Deserialize<PolicyState>(plaintext, JsonOptions);
        return state ?? throw new InvalidDataException("Policy state could not be deserialized.");
    }

    public void Save(PolicyState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (PathLocks.GetOrAdd(_path, static _ => new object()))
        {
            SaveCore(state);
        }
    }

    private void SaveCore(PolicyState state)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var encrypted = _protector.Protect(plaintext);
        var temporaryPath = _path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
