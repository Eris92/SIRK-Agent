using System.Security.Cryptography;
using System.Text.Json;
using SirkAgent.Policy;

namespace SirkAgent.Service.Core;

internal sealed class QuarantineStore
{
    private readonly AgentPaths _paths;
    private readonly IStateProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public QuarantineStore(AgentPaths paths, IStateProtector protector)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public QuarantineLoadResult Load()
    {
        try
        {
            if (File.Exists(_paths.QuarantineProtectedPath))
            {
                var encrypted = File.ReadAllBytes(_paths.QuarantineProtectedPath);
                if (encrypted.Length == 0)
                    throw new InvalidDataException("Protected quarantine state is empty.");

                var plaintext = _protector.Unprotect(encrypted);
                var state = JsonSerializer.Deserialize<QuarantineState>(plaintext, _jsonOptions)
                            ?? throw new InvalidDataException("Protected quarantine state could not be deserialized.");
                return new QuarantineLoadResult(state, false, null);
            }

            if (File.Exists(_paths.LegacyQuarantinePath))
            {
                var state = JsonSerializer.Deserialize<QuarantineState>(File.ReadAllBytes(_paths.LegacyQuarantinePath), _jsonOptions)
                            ?? throw new InvalidDataException("Legacy quarantine state could not be deserialized.");
                Save(state);
                File.Move(_paths.LegacyQuarantinePath, _paths.LegacyQuarantinePath + ".migrated", overwrite: true);
                return new QuarantineLoadResult(state, false, "LEGACY_STATE_MIGRATED");
            }

            return new QuarantineLoadResult(QuarantineState.Inactive, false, null);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            PreserveCorruptedFile();
            var timestamp = DateTimeOffset.UtcNow;
            var recovered = new QuarantineState(
                true,
                timestamp,
                "QUARANTINE_STATE_TAMPER",
                "Startup",
                timestamp,
                exception.GetType().Name,
                "Startup",
                1);
            Save(recovered);
            return new QuarantineLoadResult(recovered, true, exception.ToString());
        }
    }

    public void Save(QuarantineState state)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        AtomicFile.Write(_paths.QuarantineProtectedPath, _protector.Protect(plaintext));
    }

    private void PreserveCorruptedFile()
    {
        if (!File.Exists(_paths.QuarantineProtectedPath))
            return;

        var evidencePath = Path.Combine(
            _paths.AgentDirectory,
            $"quarantine-state.tampered.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bin");
        File.Copy(_paths.QuarantineProtectedPath, evidencePath, overwrite: false);
    }
}

internal sealed record QuarantineLoadResult(QuarantineState State, bool TamperDetected, string? Error);

internal sealed record QuarantineState(
    bool Active,
    DateTimeOffset? SinceUtc,
    string? Reason,
    string? Trigger,
    DateTimeOffset? LastUpdatedUtc,
    string? LastReason,
    string? LastTrigger,
    long DetectionCount)
{
    public static QuarantineState Inactive { get; } = new(false, null, null, null, null, null, null, 0);
}
