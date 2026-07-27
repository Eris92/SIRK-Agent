using System.Text.Json;
using SirkAgent.Policy;

namespace SirkAgent.Service.Core;

internal enum TelemetryPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

internal sealed record TelemetryEnvelope(
    Guid EventId,
    DateTimeOffset TimestampUtc,
    string Category,
    string Action,
    TelemetryPriority Priority,
    int Attempt,
    DateTimeOffset? NextAttemptUtc,
    JsonElement Data);

internal sealed record TelemetryQueueItem(string Path, TelemetryEnvelope Envelope);
internal sealed record TelemetryQueueSnapshot(int Files, long Bytes, DateTimeOffset? OldestUtc,
    DateTimeOffset? NewestUtc, int CorruptFiles, int MaxFiles, long MaxBytes, int MaxAgeDays);

internal sealed class TelemetryQueue
{
    private readonly string _directory;
    private readonly IStateProtector _protector;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _normalCycleInterval;
    private readonly JsonSerializerOptions _jsonOptions;

    public TelemetryQueue(string directory, IStateProtector protector, long maxBytes,
        JsonSerializerOptions jsonOptions, int maxFiles = 5000, int maxAgeDays = 14,
        int normalCycleIntervalMinutes = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        if (maxBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxFiles < 100)
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        if (maxAgeDays < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAgeDays));
        if (normalCycleIntervalMinutes < 1)
            throw new ArgumentOutOfRangeException(nameof(normalCycleIntervalMinutes));

        _directory = directory;
        _protector = protector;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        _maxAge = TimeSpan.FromDays(maxAgeDays);
        _normalCycleInterval = TimeSpan.FromMinutes(normalCycleIntervalMinutes);
        _jsonOptions = jsonOptions;
        Directory.CreateDirectory(_directory);
        EnforceRetention();
    }

    public TelemetryEnvelope Enqueue(string category, string action, TelemetryPriority priority, object data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(data);

        var envelope = new TelemetryEnvelope(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            category,
            action,
            priority,
            0,
            null,
            JsonSerializer.SerializeToElement(data, _jsonOptions));

        if (!ShouldThrottle(envelope))
            WriteEnvelope(envelope);

        EnforceRetention();
        return envelope;
    }

    public IReadOnlyList<string> SnapshotFiles() =>
        Directory.EnumerateFiles(_directory, "*.bin", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public TelemetryQueueSnapshot Snapshot()
    {
        var files = SnapshotFiles().Select(path => new FileInfo(path)).OrderBy(file => file.CreationTimeUtc).ToArray();
        var corrupt = Directory.EnumerateFiles(_directory, "*.corrupt.*", SearchOption.TopDirectoryOnly).Count();
        return new TelemetryQueueSnapshot(files.Length, files.Sum(file => file.Length),
            files.FirstOrDefault()?.CreationTimeUtc, files.LastOrDefault()?.CreationTimeUtc,
            corrupt, _maxFiles, _maxBytes, (int)_maxAge.TotalDays);
    }

    public IReadOnlyList<TelemetryQueueItem> ReadReady(int maximum, DateTimeOffset utcNow)
    {
        if (maximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));

        var result = new List<TelemetryQueueItem>();
        foreach (var path in SnapshotFiles())
        {
            try
            {
                var plaintext = _protector.Unprotect(File.ReadAllBytes(path));
                var envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(plaintext, _jsonOptions)
                    ?? throw new InvalidDataException("Telemetry envelope deserialized to null.");
                if (envelope.NextAttemptUtc is null || envelope.NextAttemptUtc <= utcNow)
                    result.Add(new TelemetryQueueItem(path, envelope));
            }
            catch
            {
                PreserveCorrupt(path);
            }

            if (result.Count >= maximum)
                break;
        }

        return result;
    }

    public void Complete(TelemetryQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (File.Exists(item.Path))
            File.Delete(item.Path);
    }

    public void Retry(TelemetryQueueItem item, DateTimeOffset nextAttemptUtc)
    {
        ArgumentNullException.ThrowIfNull(item);
        var updated = item.Envelope with
        {
            Attempt = checked(item.Envelope.Attempt + 1),
            NextAttemptUtc = nextAttemptUtc
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(updated, _jsonOptions);
        AtomicFile.Write(item.Path, _protector.Protect(plaintext));
    }

    public long TotalBytes() => SnapshotFiles().Sum(path => new FileInfo(path).Length);

    private bool ShouldThrottle(TelemetryEnvelope envelope)
    {
        if (!string.Equals(envelope.Category, "Agent", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(envelope.Action, "CycleCompleted", StringComparison.OrdinalIgnoreCase) ||
            envelope.Priority > TelemetryPriority.Normal)
            return false;

        var newest = SnapshotFiles().LastOrDefault();
        if (newest is null)
            return false;

        try
        {
            var age = DateTimeOffset.UtcNow - File.GetCreationTimeUtc(newest);
            return age < _normalCycleInterval;
        }
        catch
        {
            return false;
        }
    }

    private void WriteEnvelope(TelemetryEnvelope envelope)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions);
        var protectedBytes = _protector.Protect(plaintext);
        var fileName = $"{envelope.TimestampUtc:yyyyMMddHHmmssfffffff}-{(int)envelope.Priority}-{envelope.EventId:N}.bin";
        AtomicFile.Write(Path.Combine(_directory, fileName), protectedBytes);
    }

    private void PreserveCorrupt(string path)
    {
        var destination = path + $".corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        try { File.Move(path, destination, overwrite: false); }
        catch { }
    }

    private void EnforceRetention()
    {
        var now = DateTimeOffset.UtcNow;
        var files = SnapshotFiles()
            .Select(path => new QueueFile(path, ParsePriority(path), new FileInfo(path).Length,
                File.GetCreationTimeUtc(path)))
            .OrderBy(file => file.CreatedUtc)
            .ToList();

        foreach (var file in files.Where(file => file.Priority < TelemetryPriority.Critical &&
                                                 now - file.CreatedUtc > _maxAge).ToArray())
        {
            TryDelete(file.Path);
            files.Remove(file);
        }

        while (files.Count > _maxFiles)
        {
            var candidate = files.FirstOrDefault(file => file.Priority < TelemetryPriority.Critical);
            if (candidate is null)
                break;
            TryDelete(candidate.Path);
            files.Remove(candidate);
        }

        var total = files.Sum(file => file.Length);
        foreach (var file in files.Where(file => file.Priority < TelemetryPriority.Critical)
                     .OrderBy(file => file.Priority)
                     .ThenBy(file => file.CreatedUtc))
        {
            if (total <= _maxBytes)
                break;
            if (TryDelete(file.Path))
                total -= file.Length;
        }

        if (total > _maxBytes)
            throw new IOException("Telemetry queue limit exceeded by critical events; no critical event was deleted.");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static TelemetryPriority ParsePriority(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var value) && Enum.IsDefined(typeof(TelemetryPriority), value)
            ? (TelemetryPriority)value
            : TelemetryPriority.Normal;
    }

    private sealed record QueueFile(string Path, TelemetryPriority Priority, long Length, DateTimeOffset CreatedUtc);
}