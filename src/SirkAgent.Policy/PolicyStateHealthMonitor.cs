namespace SirkAgent.Policy;

public sealed record PolicyStateHealthResult(bool IsHealthy, string Code, string Message);

public sealed class PolicyStateHealthMonitor
{
    private readonly IPolicyStateStore _store;
    private readonly string _statePath;

    public PolicyStateHealthMonitor(IPolicyStateStore store, string statePath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _statePath = string.IsNullOrWhiteSpace(statePath)
            ? throw new ArgumentException("State path is required.", nameof(statePath))
            : Path.GetFullPath(statePath);
    }

    public PolicyStateHealthResult Check()
    {
        if (!File.Exists(_statePath))
            return new PolicyStateHealthResult(false, "STATE_MISSING", "Policy state file does not exist.");

        try
        {
            if (new FileInfo(_statePath).Length == 0)
                return new PolicyStateHealthResult(false, "STATE_EMPTY", "Policy state file is empty.");

            var state = _store.Load();
            if (state.Epoch < 0 || state.Version < 0)
                return new PolicyStateHealthResult(false, "STATE_INVALID_COUNTERS", "Policy counters are invalid.");

            if (state.Version > 0 && string.IsNullOrWhiteSpace(state.ActivePolicyHash))
                return new PolicyStateHealthResult(false, "STATE_HASH_MISSING", "Active policy hash is missing.");

            return new PolicyStateHealthResult(true, "OK", "Policy state is healthy.");
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            return new PolicyStateHealthResult(false, "STATE_UNPROTECT_FAILED", exception.Message);
        }
        catch (InvalidDataException exception)
        {
            return new PolicyStateHealthResult(false, "STATE_INVALID", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PolicyStateHealthResult(false, "STATE_IO_FAILED", exception.Message);
        }
        catch (System.Text.Json.JsonException exception)
        {
            return new PolicyStateHealthResult(false, "STATE_JSON_INVALID", exception.Message);
        }
    }
}