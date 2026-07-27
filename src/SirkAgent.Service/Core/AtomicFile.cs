using System.Text.Json;

namespace SirkAgent.Service.Core;

internal static class AtomicFile
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonSerializerDefaults.Web);

    public static void Write(string path, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static void WriteBytes(string path, ReadOnlySpan<byte> content) => Write(path, content);

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
        => Write(path, JsonSerializer.SerializeToUtf8Bytes(value, options));

    public static void AppendJsonLine<T>(string path, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var line = JsonSerializer.Serialize(value, CompactJsonOptions);
        File.AppendAllText(path, line + Environment.NewLine);
    }
}
