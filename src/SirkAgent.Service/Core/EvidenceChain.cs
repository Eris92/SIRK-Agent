using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SirkAgent.Policy;

namespace SirkAgent.Service.Core;

internal sealed record EvidenceEvent(
    Guid EventId,
    DateTimeOffset TimestampUtc,
    long MonotonicSequence,
    string TenantId,
    string DeviceId,
    string Category,
    string Action,
    JsonElement Data,
    string? PreviousEventHash,
    string EventHash);

internal sealed record EvidenceChainState(long LastSequence, string? LastHash);
internal sealed record EvidenceValidationResult(bool IsValid, string Code, long EventsChecked, string? Error);

internal sealed class EvidenceChain
{
    private readonly string _logPath;
    private readonly string _statePath;
    private readonly IStateProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _sync = new();

    public EvidenceChain(string logPath, string statePath, IStateProtector protector,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _logPath = logPath;
        _statePath = statePath;
        _protector = protector;
        _jsonOptions = jsonOptions;
    }

    public EvidenceEvent Append(string tenantId, string deviceId, string category, string action, object data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(data);

        lock (_sync)
        {
            var state = LoadState();
            var sequence = checked(state.LastSequence + 1);
            var timestamp = DateTimeOffset.UtcNow;
            var eventId = Guid.NewGuid();
            var element = JsonSerializer.SerializeToElement(data, _jsonOptions);
            var canonical = JsonSerializer.SerializeToUtf8Bytes(new
            {
                eventId,
                timestampUtc = timestamp,
                monotonicSequence = sequence,
                tenantId,
                deviceId,
                category,
                action,
                data = element,
                previousEventHash = state.LastHash
            }, _jsonOptions);
            var hash = Convert.ToBase64String(SHA256.HashData(canonical));
            var evidence = new EvidenceEvent(eventId, timestamp, sequence, tenantId, deviceId,
                category, action, element, state.LastHash, hash);

            AtomicFile.AppendJsonLine(_logPath, evidence);
            SaveState(new EvidenceChainState(sequence, hash));
            return evidence;
        }
    }

    public EvidenceValidationResult Validate()
    {
        if (!File.Exists(_logPath))
            return new EvidenceValidationResult(true, "EVIDENCE_EMPTY", 0, null);

        try
        {
            long expectedSequence = 1;
            string? previousHash = null;
            long checkedEvents = 0;

            foreach (var line in File.ReadLines(_logPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var evidence = JsonSerializer.Deserialize<EvidenceEvent>(line, _jsonOptions)
                    ?? throw new InvalidDataException("Evidence event deserialized to null.");
                if (evidence.MonotonicSequence != expectedSequence)
                    return new EvidenceValidationResult(false, "EVIDENCE_SEQUENCE_GAP", checkedEvents,
                        $"Expected sequence {expectedSequence}, got {evidence.MonotonicSequence}.");
                if (!string.Equals(evidence.PreviousEventHash, previousHash, StringComparison.Ordinal))
                    return new EvidenceValidationResult(false, "EVIDENCE_PREVIOUS_HASH_MISMATCH", checkedEvents,
                        $"Previous hash mismatch at sequence {evidence.MonotonicSequence}.");

                var canonical = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    eventId = evidence.EventId,
                    timestampUtc = evidence.TimestampUtc,
                    monotonicSequence = evidence.MonotonicSequence,
                    tenantId = evidence.TenantId,
                    deviceId = evidence.DeviceId,
                    category = evidence.Category,
                    action = evidence.Action,
                    data = evidence.Data,
                    previousEventHash = evidence.PreviousEventHash
                }, _jsonOptions);
                var calculated = Convert.ToBase64String(SHA256.HashData(canonical));
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromBase64String(calculated), Convert.FromBase64String(evidence.EventHash)))
                    return new EvidenceValidationResult(false, "EVIDENCE_HASH_MISMATCH", checkedEvents,
                        $"Event hash mismatch at sequence {evidence.MonotonicSequence}.");

                previousHash = evidence.EventHash;
                expectedSequence++;
                checkedEvents++;
            }

            var state = LoadState();
            if (state.LastSequence != checkedEvents || !string.Equals(state.LastHash, previousHash, StringComparison.Ordinal))
                return new EvidenceValidationResult(false, "EVIDENCE_STATE_MISMATCH", checkedEvents,
                    "Protected evidence state does not match the append-only log.");

            return new EvidenceValidationResult(true, "EVIDENCE_CHAIN_OK", checkedEvents, null);
        }
        catch (Exception ex)
        {
            return new EvidenceValidationResult(false, "EVIDENCE_VALIDATION_FAILED", 0, ex.ToString());
        }
    }

    private EvidenceChainState LoadState()
    {
        if (!File.Exists(_statePath))
            return new EvidenceChainState(0, null);
        var protectedBytes = File.ReadAllBytes(_statePath);
        var plaintext = _protector.Unprotect(protectedBytes);
        return JsonSerializer.Deserialize<EvidenceChainState>(plaintext, _jsonOptions)
            ?? throw new InvalidDataException("Evidence chain state deserialized to null.");
    }

    private void SaveState(EvidenceChainState state)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        AtomicFile.WriteBytes(_statePath, _protector.Protect(plaintext));
    }
}
