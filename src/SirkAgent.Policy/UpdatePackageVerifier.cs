using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirkAgent.Policy;

public sealed record UpdateManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("applicationId")] string ApplicationId,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("files")] IReadOnlyList<UpdateManifestFile> Files,
    [property: JsonPropertyName("signature")] PolicySignature Signature);

public sealed record UpdateManifestFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record UpdateVerificationResult(bool Accepted, string Code, string Message)
{
    public static UpdateVerificationResult Success() =>
        new(true, "UPDATE_VERIFIED", "Signed update package verified.");

    public static UpdateVerificationResult Reject(string code, string message) =>
        new(false, code, message);
}

public sealed class UpdatePackageVerifier
{
    private const int MaximumFiles = 8192;
    private readonly IPolicyPublicKeyProvider _keys;
    private readonly JsonSerializerOptions _json;

    public UpdatePackageVerifier(
        IPolicyPublicKeyProvider keys,
        JsonSerializerOptions? json = null)
    {
        _keys = keys;
        _json = json ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public UpdateVerificationResult Verify(
        string packageDirectory,
        UpdateManifest manifest,
        string? currentVersion = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (manifest.Files is null ||
            manifest.Signature is null ||
            manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.ApplicationId, "sirk-agent", StringComparison.Ordinal) ||
            !string.Equals(manifest.Product, "SIRK Agent", StringComparison.Ordinal) ||
            !string.Equals(manifest.Runtime, "win-x64", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Files.Count is <= 0 or > MaximumFiles)
            return UpdateVerificationResult.Reject(
                "UPDATE_MANIFEST_INVALID",
                "Update manifest metadata is invalid.");

        if (!string.IsNullOrWhiteSpace(currentVersion) &&
            CompareVersions(manifest.Version, currentVersion) <= 0)
            return UpdateVerificationResult.Reject(
                "UPDATE_VERSION_ROLLBACK",
                $"Update version {manifest.Version} is not newer than installed version {currentVersion}.");
        if (!string.Equals(manifest.Signature.Algorithm, "ES256", StringComparison.Ordinal))
            return UpdateVerificationResult.Reject(
                "UPDATE_SIGNATURE_ALGORITHM",
                "Only ES256 update signatures are accepted.");

        ECDsa? trustedKey;
        try
        {
            trustedKey = _keys.GetKey(manifest.Signature.KeyId);
        }
        catch (CryptographicException)
        {
            return UpdateVerificationResult.Reject(
                "UPDATE_KEY_INVALID",
                "Trusted update key is invalid.");
        }
        using var key = trustedKey;
        if (key is null)
            return UpdateVerificationResult.Reject(
                "UPDATE_KEY_UNKNOWN",
                "Update signing key is not trusted.");

        byte[] signature;
        try
        {
            signature = DecodeBase64Url(manifest.Signature.Value);
        }
        catch (FormatException)
        {
            return UpdateVerificationResult.Reject(
                "UPDATE_SIGNATURE_ENCODING",
                "Update signature is invalid.");
        }
        try
        {
            if (signature.Length != 64 ||
                !key.VerifyData(
                    CanonicalJson.SerializeWithoutTopLevelSignature(manifest),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return UpdateVerificationResult.Reject(
                    "UPDATE_SIGNATURE_INVALID",
                    "Update manifest signature verification failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relative = NormalizeRelativePath(file.Path);
            if (relative is null ||
                !seen.Add(relative) ||
                file.Size < 0 ||
                !IsSha256(file.Sha256))
                return UpdateVerificationResult.Reject(
                    "UPDATE_FILE_ENTRY_INVALID",
                    "Update file entry is invalid.");

            var target = Path.GetFullPath(Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(target))
                return UpdateVerificationResult.Reject(
                    "UPDATE_FILE_MISSING",
                    $"Update file is missing: {relative}");

            var info = new FileInfo(target);
            if (info.Length != file.Size)
                return UpdateVerificationResult.Reject(
                    "UPDATE_FILE_SIZE_MISMATCH",
                    $"Update file size mismatch: {relative}");
            using var stream = File.OpenRead(target);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return UpdateVerificationResult.Reject(
                    "UPDATE_FILE_HASH_MISMATCH",
                    $"Update file hash mismatch: {relative}");
        }

        foreach (var actualPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(root, actualPath));
            if (relative is null)
                return UpdateVerificationResult.Reject(
                    "UPDATE_FILE_ENTRY_INVALID",
                    "Update payload contains an unsafe file path.");
            if (string.Equals(relative, "update-manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Contains(relative))
                return UpdateVerificationResult.Reject(
                    "UPDATE_FILE_UNLISTED",
                    $"Update payload contains an unlisted file: {relative}");
        }

        var required = new[]
        {
            "SirkAgent.Service.exe",
            "SirkAgent.Service.dll",
            "SirkAgent.Policy.dll",
            "sirkctl.exe"
        };
        if (required.Any(name => !seen.Contains(name)))
            return UpdateVerificationResult.Reject(
                "UPDATE_REQUIRED_FILE_MISSING",
                "Update package does not contain every required Agent runtime file.");
        return UpdateVerificationResult.Success();
    }

    public UpdateVerificationResult Verify(string packageDirectory, string manifestPath)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                File.ReadAllBytes(manifestPath),
                _json);
            return manifest is null
                ? UpdateVerificationResult.Reject(
                    "UPDATE_MANIFEST_INVALID",
                    "Update manifest could not be read.")
                : Verify(packageDirectory, manifest);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return UpdateVerificationResult.Reject(
                "UPDATE_MANIFEST_INVALID",
                error.Message);
        }
    }

    private static string? NormalizeRelativePath(string? value)
    {
        var path = (value ?? string.Empty).Replace('\\', '/');
        if (path.Length is <= 0 or > 512 ||
            path.StartsWith('/') ||
            Path.IsPathRooted(path) ||
            path.Contains(':') ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
            return null;
        return path;
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static int CompareVersions(string candidate, string current)
    {
        static (Version Core, string? PreRelease) Parse(string value)
        {
            var withoutBuild = value.Split('+', 2)[0];
            var parts = withoutBuild.Split('-', 2);
            if (!Version.TryParse(parts[0], out var core))
                throw new FormatException($"Invalid update version: {value}");
            return (core, parts.Length == 2 ? parts[1] : null);
        }

        try
        {
            var left = Parse(candidate);
            var right = Parse(current);
            var core = left.Core.CompareTo(right.Core);
            if (core != 0) return core;
            if (left.PreRelease is null && right.PreRelease is null) return 0;
            if (left.PreRelease is null) return 1;
            if (right.PreRelease is null) return -1;
            return StringComparer.OrdinalIgnoreCase.Compare(
                left.PreRelease,
                right.PreRelease);
        }
        catch (FormatException)
        {
            return string.Compare(candidate, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => "",
            2 => "==",
            3 => "=",
            _ => throw new FormatException()
        };
        return Convert.FromBase64String(padded);
    }
}
