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

internal sealed class TelemetryQueue
{
    private readonly string _directory;
    private readonly IStateProtector _protector;
    private readonly long _maxBytes;
    private readonly JsonSerializerOptions _jsonOptions;

    public TelemetryQueue(string directory, IStateProtector protector, long maxBytes,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        if (maxBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        _directory = directory;
        _protector = protector;
        _maxBytes = maxBytes;
        _jsonOptions = jsonOptions;
        Directory.CreateDirectory(_directory);
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

        WriteEnvelope(envelope);
        EnforceLimit();
        return envelope;
    }

    public IReadOnlyList<string> SnapshotFiles() =>
        Directory.EnumerateFiles(_directory, "*.bin", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private void EnforceLimit()
    {
        var files = SnapshotFiles()
            .Select(path => new QueueFile(path, ParsePriority(path), new FileInfo(path).Length))
            .ToList();
        var total = files.Sum(file => file.Length);
        if (total <= _maxBytes)
            return;

        foreach (var file in files
                     .Where(file => file.Priority < TelemetryPriority.Critical)
                     .OrderBy(file => file.Priority)
                     .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            File.Delete(file.Path);
            total -= file.Length;
            if (total <= _maxBytes)
                return;
        }

        if (total > _maxBytes)
            throw new IOException("Telemetry queue limit exceeded by critical events; no critical event was deleted.");
    }

    private static TelemetryPriority ParsePriority(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out var value) && Enum.IsDefined(typeof(TelemetryPriority), value)
            ? (TelemetryPriority)value
            : TelemetryPriority.Normal;
    }

    private sealed record QueueFile(string Path, TelemetryPriority Priority, long Length);
}
