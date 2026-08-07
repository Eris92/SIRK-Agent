using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SirkAgent.Policy;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        if (args.Length == 6 && args[0].Equals("package", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetFullPath(args[1]);
            var version = ValidateVersion(args[2]);
            var runtime = ValidateRuntime(args[3]);
            var keyFile = Path.GetFullPath(args[4]);
            var keyId = ValidateKeyId(args[5]);
            var manifest = SignPackage(directory, version, runtime, keyFile, keyId);
            var output = Path.Combine(directory, "update-manifest.json");
            await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions()));
            Console.WriteLine(output);
            return 0;
        }

        if (args.Length == 8 && args[0].Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            var asset = Path.GetFullPath(args[1]);
            var version = ValidateVersion(args[2]);
            var runtime = ValidateRuntime(args[3]);
            var channel = ValidateChannel(args[4]);
            var keyFile = Path.GetFullPath(args[5]);
            var keyId = ValidateKeyId(args[6]);
            var output = Path.GetFullPath(args[7]);
            var descriptor = SignRelease(asset, version, runtime, channel, keyFile, keyId);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(descriptor, JsonOptions()));
            Console.WriteLine(output);
            return 0;
        }

        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  package <directory> <version> <runtime> <private-key.pem> <key-id>");
        Console.Error.WriteLine("  release <asset.zip> <version> <runtime> <channel> <private-key.pem> <key-id> <descriptor.json>");
        return 2;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

static JsonSerializerOptions JsonOptions() =>
    new(JsonSerializerDefaults.Web) { WriteIndented = true };

static UpdateManifest SignPackage(string directory, string version, string runtime, string keyFile, string keyId)
{
    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
    var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFileName(path).Equals("update-manifest.json", StringComparison.OrdinalIgnoreCase))
        .Select(path => new UpdateManifestFile(
            Path.GetRelativePath(directory, path).Replace('\\', '/'),
            Sha256(path)))
        .OrderBy(file => file.Path, StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0) throw new InvalidDataException("Package directory is empty.");

    var unsigned = new UpdateManifest(
        1,
        "SIRK Agent",
        version,
        runtime,
        files,
        EmptySignature(keyId));
    return unsigned with { Signature = Sign(unsigned, keyFile, keyId) };
}

static AgentReleaseDescriptor SignRelease(
    string asset,
    string version,
    string runtime,
    string channel,
    string keyFile,
    string keyId)
{
    var info = new FileInfo(asset);
    if (!info.Exists || info.Length <= 0) throw new FileNotFoundException("Release asset is missing.", asset);
    var unsigned = new AgentReleaseDescriptor(
        1,
        "SIRK Agent",
        version,
        runtime,
        channel,
        info.Name,
        info.Length,
        Sha256(asset),
        DateTimeOffset.UtcNow,
        EmptySignature(keyId));
    return unsigned with { Signature = Sign(unsigned, keyFile, keyId) };
}

static PolicySignature Sign<T>(T value, string keyFile, string keyId)
{
    var pem = File.ReadAllText(keyFile);
    using var key = ECDsa.Create();
    key.ImportFromPem(pem);
    if (key.KeySize != 256) throw new CryptographicException("Release signing key must be P-256.");
    var signature = key.SignData(
        CanonicalJson.SerializeWithoutTopLevelSignature(value),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return new PolicySignature { Algorithm = "ES256", KeyId = keyId, Value = Base64Url(signature) };
}

static PolicySignature EmptySignature(string keyId) =>
    new() { Algorithm = "ES256", KeyId = keyId, Value = string.Empty };

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static string ValidateVersion(string value)
{
    value = value.Trim();
    if (!Regex.IsMatch(value, "^0\\.1\\.1\\.[0-9]+$", RegexOptions.CultureInvariant))
        throw new InvalidDataException("Release version must use 0.1.1.X and remain below 1.0.0.");
    return value;
}

static string ValidateRuntime(string value) => value == "win-x64"
    ? value
    : throw new InvalidDataException("Only win-x64 is supported by the Agent update contract.");

static string ValidateChannel(string value) => value is "stable" or "preview"
    ? value
    : throw new InvalidDataException("Update channel must be stable or preview.");

static string ValidateKeyId(string value)
{
    value = value.Trim();
    if (!Regex.IsMatch(value, "^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant))
        throw new InvalidDataException("Invalid release signing key id.");
    return value;
}

static string Base64Url(byte[] value) => Convert.ToBase64String(value)
    .TrimEnd('=')
    .Replace('+', '-')
    .Replace('/', '_');

internal sealed record AgentReleaseDescriptor(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("assetName")] string AssetName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("publishedAtUtc")] DateTimeOffset PublishedAtUtc,
    [property: JsonPropertyName("signature")] PolicySignature Signature);
