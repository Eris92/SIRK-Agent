namespace SirkAgent.Policy;

public enum PolicyStateHealthStatus
{
    Ok,
    Missing,
    Empty,
    Corrupt,
    ProtectionError,
    IoError
}

public sealed record PolicyStateHealthResult(
    PolicyStateHealthStatus Status,
    PolicyState? State,
    string Code,
    string Message)
{
    public bool IsHealthy => Status == PolicyStateHealthStatus.Ok;
}

public sealed class PolicyStateHealthChecker
{
    private readonly string _path;
    private readonly IPolicyStateStore _store;

    public PolicyStateHealthChecker(string path, IPolicyStateStore store)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("State path is required.", nameof(path));

        _path = Path.GetFullPath(path);
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public PolicyStateHealthResult Check()
    {
        try
        {
            if (!File.Exists(_path))
                return new(PolicyStateHealthStatus.Missing, null, "STATE_MISSING", "Policy state file does not exist.");

            var info = new FileInfo(_path);
            if (info.Length == 0)
                return new(PolicyStateHealthStatus.Empty, null, "STATE_EMPTY", "Policy state file is empty.");

            var state = _store.Load();
            if (state.Epoch < 0 || state.Version < 0)
                return new(PolicyStateHealthStatus.Corrupt, null, "STATE_INVALID", "Policy state contains invalid counters.");

            if (state.Version > 0 && string.IsNullOrWhiteSpace(state.ActivePolicyHash))
                return new(PolicyStateHealthStatus.Corrupt, null, "STATE_HASH_MISSING", "Accepted policy state has no active policy hash.");

            return new(PolicyStateHealthStatus.Ok, state, "OK", "Policy state is healthy.");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return new(PolicyStateHealthStatus.ProtectionError, null, "STATE_UNPROTECT_FAILED", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new(PolicyStateHealthStatus.Corrupt, null, "STATE_CORRUPT", ex.Message);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new(PolicyStateHealthStatus.Corrupt, null, "STATE_JSON_INVALID", ex.Message);
        }
        catch (IOException ex)
        {
            return new(PolicyStateHealthStatus.IoError, null, "STATE_IO_ERROR", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(PolicyStateHealthStatus.IoError, null, "STATE_ACCESS_DENIED", ex.Message);
        }
    }
}
