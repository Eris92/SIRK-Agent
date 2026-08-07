using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed class AgentUpdateWorker(ILogger<AgentUpdateWorker> logger) : BackgroundService
{
    private const long MaximumPackageBytes = 80L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex VersionPattern = new(
        "^0\\.1\\.1\\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression =
            DecompressionMethods.GZip |
            DecompressionMethods.Deflate |
            DecompressionMethods.Brotli,
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        var paths = ManagementPaths.CreateDefault();
        paths.EnsureDirectories();
        var updatesRoot = Path.Combine(paths.Root, "Updates");
        Directory.CreateDirectory(updatesRoot);
        var statusPath = Path.Combine(updatesRoot, "status.json");
        var scheduler = new AgentScheduler(
            paths.Root,
            Path.GetFileName(paths.PortalCredentialPath),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromSeconds(2),
            runOnce: false);

        await foreach (var trigger in scheduler.RunAsync(stoppingToken))
        {
            try
            {
                await CheckForUpdateAsync(
                    paths,
                    updatesRoot,
                    statusPath,
                    trigger.Name,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                WriteStatus(
                    statusPath,
                    "warning",
                    "UPDATE_CHECK_FAILED",
                    CurrentVersion(),
                    null,
                    "Update check failed: " + error.GetType().Name,
                    trigger.Name);
                logger.LogWarning(
                    "Agent update check failed: {Reason}.",
                    error.GetType().Name);
            }
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private async Task CheckForUpdateAsync(
        ManagementPaths paths,
        string updatesRoot,
        string statusPath,
        string trigger,
        CancellationToken cancellationToken)
    {
        var currentVersion = CurrentVersion();
        if (!VersionPattern.IsMatch(currentVersion))
        {
            WriteStatus(
                statusPath,
                "disabled",
                "UPDATE_VERSION_UNSUPPORTED",
                currentVersion,
                null,
                "Installed Agent version is outside the 0.1.1.X pre-1.0 update line.",
                trigger);
            return;
        }

        var releaseKeysPath = Path.Combine(
            AppContext.BaseDirectory,
            "release-trusted-keys.json");
        if (!File.Exists(releaseKeysPath))
        {
            WriteStatus(
                statusPath,
                "blocked",
                "UPDATE_RELEASE_TRUST_MISSING",
                currentVersion,
                null,
                "Dedicated release trust keyring is missing.",
                trigger);
            return;
        }

        var credential = new PortalCredentialStore(
                paths.PortalCredentialPath,
                new DpapiMachineStateProtector())
            .Load();
        if (credential is null)
        {
            WriteStatus(
                statusPath,
                "idle",
                "UPDATE_PORTAL_CREDENTIAL_MISSING",
                currentVersion,
                null,
                "Agent is not enrolled with a Portal.",
                trigger);
            return;
        }

        var portal = ValidateEndpoint(
            credential.Endpoint,
            allowLoopbackHttp: true,
            "Portal endpoint");
        const string channel = "stable";
        var accessUri = CanonicalEndpoint(
            portal,
            "/api/v1/agent/update-access",
            $"runtime=win-x64&channel={Uri.EscapeDataString(channel)}&currentVersion={Uri.EscapeDataString(currentVersion)}");
        using var accessRequest = new HttpRequestMessage(HttpMethod.Get, accessUri);
        var signingKey = SHA256.HashData(Encoding.UTF8.GetBytes(credential.DeviceToken));
        try
        {
            SignPortalRequest(accessRequest, signingKey, credential.DeviceId);
            using var accessResponse = await _http.SendAsync(
                accessRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var accessBytes = await ReadBoundedAsync(
                accessResponse.Content,
                64 * 1024,
                cancellationToken);
            if (!VerifyPortalResponse(accessResponse, accessBytes, signingKey))
                throw new CryptographicException(
                    "Portal update access response signature is invalid.");
            if (!accessResponse.IsSuccessStatusCode)
                throw new HttpRequestException(
                    "Portal rejected update access with status " +
                    (int)accessResponse.StatusCode);

            var access = JsonSerializer.Deserialize<AgentUpdateAccessGrant>(accessBytes, Json)
                         ?? throw new InvalidDataException(
                             "Portal update access response is invalid.");
            var central = ValidateEndpoint(
                access.CentralBaseUrl,
                allowLoopbackHttp: false,
                "Central update endpoint");
            if (access.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                string.IsNullOrWhiteSpace(access.Ticket))
                throw new InvalidDataException(
                    "Central discovery capability is expired or missing.");

            var latestUri = CanonicalEndpoint(
                central,
                "/api/v1/agent-updates/latest",
                $"runtime=win-x64&channel={Uri.EscapeDataString(channel)}");
            using var latestRequest = new HttpRequestMessage(HttpMethod.Get, latestUri);
            latestRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", access.Ticket);
            using var latestResponse = await _http.SendAsync(
                latestRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (latestResponse.StatusCode == HttpStatusCode.NoContent)
            {
                WriteStatus(
                    statusPath,
                    "healthy",
                    "UPDATE_CURRENT",
                    currentVersion,
                    currentVersion,
                    "No newer verified Agent update is available.",
                    trigger);
                return;
            }

            var latestBytes = await ReadBoundedAsync(
                latestResponse.Content,
                256 * 1024,
                cancellationToken);
            if (!latestResponse.IsSuccessStatusCode)
                throw new HttpRequestException(
                    "Central update discovery failed with status " +
                    (int)latestResponse.StatusCode);
            var latest = JsonSerializer.Deserialize<AgentUpdateLatestResponse>(latestBytes, Json)
                         ?? throw new InvalidDataException(
                             "Central latest update response is invalid.");
            ValidateLatest(latest, currentVersion, channel, releaseKeysPath);

            var pending = Path.Combine(updatesRoot, "Pending");
            Directory.CreateDirectory(pending);
            var temporaryPackage = Path.Combine(
                pending,
                ".download-" + Guid.NewGuid().ToString("N") + ".zip");
            var staging = Path.Combine(
                pending,
                ".verify-" + Guid.NewGuid().ToString("N"));
            try
            {
                var downloadUri = CanonicalEndpoint(
                    central,
                    "/api/v1/agent-updates/" +
                    Uri.EscapeDataString(latest.Version) +
                    "/package",
                    string.Empty);
                using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
                downloadRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", latest.DownloadTicket);
                using var downloadResponse = await _http.SendAsync(
                    downloadRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                downloadResponse.EnsureSuccessStatusCode();
                await DownloadVerifiedSizeAsync(
                    downloadResponse.Content,
                    temporaryPackage,
                    latest.Size,
                    cancellationToken);
                var hash = await Sha256Async(temporaryPackage, cancellationToken);
                if (!string.Equals(hash, latest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Downloaded Agent update SHA256 mismatch.");

                ExtractSafely(temporaryPackage, staging);
                var manifestPath = Path.Combine(staging, "update-manifest.json");
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException(
                        "Signed Agent update manifest is missing.");
                var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                                   await File.ReadAllBytesAsync(
                                       manifestPath,
                                       cancellationToken),
                                   Json)
                               ?? throw new InvalidDataException(
                                   "Signed Agent update manifest is invalid.");
                var verifier = new UpdatePackageVerifier(
                    PemPolicyPublicKeyProvider.Load(releaseKeysPath, Json));
                var verification = verifier.Verify(staging, manifest, currentVersion);
                if (!verification.Accepted)
                    throw new CryptographicException(
                        verification.Code + ": " + verification.Message);
                if (!string.Equals(
                        manifest.Version,
                        latest.Version,
                        StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Inner update manifest version does not match the signed release descriptor.");

                var finalPackage = Path.Combine(
                    pending,
                    latest.Version + "-" +
                    latest.Sha256[..12].ToLowerInvariant() +
                    ".zip");
                PublishImmutablePackage(
                    temporaryPackage,
                    finalPackage,
                    latest.Sha256);
                temporaryPackage = string.Empty;

                var updater = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "SIRK",
                    "Updater",
                    "SirkUpdater.exe");
                if (!File.Exists(updater))
                    throw new FileNotFoundException(
                        "Registered SIRK Updater is missing.",
                        updater);

                WriteStatus(
                    statusPath,
                    "handoff",
                    "UPDATE_HANDOFF",
                    currentVersion,
                    latest.Version,
                    "Verified update handed off to SIRK Updater.",
                    trigger);
                var start = new ProcessStartInfo
                {
                    FileName = updater,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(updater)!
                };
                start.ArgumentList.Add("update");
                start.ArgumentList.Add("sirk-agent");
                start.ArgumentList.Add(finalPackage);
                start.ArgumentList.Add(latest.Sha256);
                start.ArgumentList.Add(latest.Version);
                using var process = Process.Start(start)
                                    ?? throw new InvalidOperationException(
                                        "SIRK Updater process could not be started.");
                logger.LogInformation(
                    "Handed verified Agent update {Version} to SIRK Updater.",
                    latest.Version);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryPackage) &&
                    File.Exists(temporaryPackage))
                    File.Delete(temporaryPackage);
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    private static void ValidateLatest(
        AgentUpdateLatestResponse latest,
        string currentVersion,
        string channel,
        string releaseKeysPath)
    {
        if (!VersionPattern.IsMatch(latest.Version ?? string.Empty) ||
            latest.Runtime != "win-x64" ||
            latest.Channel != channel ||
            latest.Size is <= 0 or > MaximumPackageBytes ||
            !IsSha256(latest.Sha256) ||
            string.IsNullOrWhiteSpace(latest.DownloadTicket) ||
            latest.DownloadTicketExpiresAtUtc <= DateTimeOffset.UtcNow ||
            latest.Descriptor is null)
            throw new InvalidDataException(
                "Central latest update metadata is invalid.");
        if (Version.Parse(latest.Version).CompareTo(Version.Parse(currentVersion)) <= 0)
            throw new InvalidDataException(
                "Central attempted an Agent update rollback or same-version overwrite.");

        var descriptor = latest.Descriptor;
        if (descriptor.SchemaVersion != 1 ||
            descriptor.ApplicationId != "sirk-agent" ||
            descriptor.Product != "SIRK Agent" ||
            descriptor.Version != latest.Version ||
            descriptor.Runtime != latest.Runtime ||
            descriptor.Channel != latest.Channel ||
            descriptor.Size != latest.Size ||
            !string.Equals(
                descriptor.Sha256,
                latest.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            descriptor.Commit.Length != 40 ||
            descriptor.Commit.Any(character => !Uri.IsHexDigit(character)) ||
            descriptor.AssetName != Path.GetFileName(descriptor.AssetName) ||
            !descriptor.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            descriptor.Signature is null ||
            descriptor.Signature.Algorithm != "ES256")
            throw new InvalidDataException(
                "Signed Agent release descriptor does not match Central metadata.");

        using var key = PemPolicyPublicKeyProvider.Load(releaseKeysPath, Json)
                            .GetKey(descriptor.Signature.KeyId)
                        ?? throw new CryptographicException(
                            "Agent release signing key is not trusted locally.");
        if (key.KeySize != 256)
            throw new CryptographicException(
                "Agent release signing key must be P-256.");
        var signature = DecodeBase64Url(descriptor.Signature.Value);
        try
        {
            if (signature.Length != 64 ||
                !key.VerifyData(
                    CanonicalJson.SerializeWithoutTopLevelSignature(descriptor),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException(
                    "Agent release descriptor ES256 signature verification failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static void SignPortalRequest(
        HttpRequestMessage request,
        byte[] signingKey,
        string deviceId)
    {
        var timestamp = DateTimeOffset.UtcNow
            .ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(18));
        var path = request.RequestUri?.PathAndQuery
                   ?? throw new InvalidOperationException(
                       "Portal update request URI is missing.");
        var bodyHash = Base64Url(SHA256.HashData(ReadOnlySpan<byte>.Empty));
        var canonical = Encoding.UTF8.GetBytes(
            $"GET\n{path}\n{timestamp}\n{nonce}\n{bodyHash}");
        var signature = HMACSHA256.HashData(signingKey, canonical);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "SIRK-Agent",
                Base64Url(Encoding.UTF8.GetBytes(deviceId)));
            request.Headers.TryAddWithoutValidation("X-SIRK-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-SIRK-Nonce", nonce);
            request.Headers.TryAddWithoutValidation(
                "X-SIRK-Signature",
                Base64Url(signature));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static bool VerifyPortalResponse(
        HttpResponseMessage response,
        byte[] body,
        byte[] signingKey)
    {
        var timestamp = Header(response, "X-SIRK-Response-Timestamp");
        var nonce = Header(response, "X-SIRK-Response-Nonce");
        var suppliedText = Header(response, "X-SIRK-Response-Signature");
        if (!long.TryParse(
                timestamp,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var milliseconds))
            return false;
        DateTimeOffset responseTime;
        try
        {
            responseTime = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        if ((DateTimeOffset.UtcNow - responseTime).Duration() > TimeSpan.FromMinutes(2))
            return false;

        byte[] supplied;
        try
        {
            supplied = DecodeBase64Url(suppliedText);
        }
        catch (FormatException)
        {
            return false;
        }
        if (supplied.Length != 32)
        {
            CryptographicOperations.ZeroMemory(supplied);
            return false;
        }
        var bodyHash = Base64Url(SHA256.HashData(body));
        var canonical = Encoding.UTF8.GetBytes(
            $"{timestamp}\n{nonce}\n{bodyHash}");
        var expected = HMACSHA256.HashData(signingKey, canonical);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, supplied);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.SingleOrDefault() ?? string.Empty
            : string.Empty;

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException(
                "Update metadata response is too large.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException(
                    "Update metadata response is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static async Task DownloadVerifiedSizeAsync(
        HttpContent content,
        string destination,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (expectedSize is <= 0 or > MaximumPackageBytes)
            throw new InvalidDataException(
                "Signed update package size is invalid.");
        if (content.Headers.ContentLength is long length && length != expectedSize)
            throw new InvalidDataException(
                "Central update package size does not match signed metadata.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > expectedSize || total > MaximumPackageBytes)
                throw new InvalidDataException(
                    "Central update package exceeded signed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        if (total != expectedSize)
            throw new InvalidDataException("Central update package is truncated.");
    }

    private static void ExtractSafely(string packagePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count is <= 0 or > 4096)
            throw new InvalidDataException(
                "Agent update ZIP entry count is invalid.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative) ||
                relative.StartsWith('/') ||
                relative.Contains(':') ||
                relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => part == "..") ||
                Path.IsPathRooted(relative) ||
                !seen.Add(relative))
                throw new InvalidDataException(
                    "Agent update ZIP contains an unsafe or duplicate path.");
            if (relative.EndsWith('/')) continue;
            total += entry.Length;
            if (entry.Length > MaximumPackageBytes || total > MaximumPackageBytes)
                throw new InvalidDataException(
                    "Agent update ZIP expands beyond the allowed size.");
            var target = Path.GetFullPath(Path.Combine(
                destination,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Agent update ZIP path escapes the verification root.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }

    private static void PublishImmutablePackage(
        string source,
        string destination,
        string expectedHash)
    {
        if (File.Exists(destination))
        {
            using var existing = File.OpenRead(destination);
            var hash = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Pending Agent update version already exists with a different hash.");
            File.Delete(source);
            return;
        }
        File.Move(source, destination, overwrite: false);
    }

    private static Uri CanonicalEndpoint(Uri source, string path, string query) =>
        new UriBuilder(source) { Path = path, Query = query }.Uri;

    private static Uri ValidateEndpoint(
        string raw,
        bool allowLoopbackHttp,
        string label)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(allowLoopbackHttp &&
               uri.Scheme == Uri.UriSchemeHttp &&
               uri.IsLoopback)))
            throw new InvalidDataException(
                label +
                " must be HTTPS (or loopback HTTP where explicitly allowed).");
        return uri;
    }

    private static string CurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var value = assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                        .InformationalVersion
                    ?? assembly.GetName().Version?.ToString()
                    ?? "unknown";
        var plus = value.IndexOf('+');
        return plus > 0 ? value[..plus] : value;
    }

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private static void WriteStatus(
        string path,
        string state,
        string code,
        string currentVersion,
        string? availableVersion,
        string message,
        string trigger) =>
        AtomicFile.WriteJson(
            path,
            new AgentUpdateStatus(
                1,
                state,
                code,
                currentVersion,
                availableVersion,
                DateTimeOffset.UtcNow,
                message,
                trigger),
            Json);
}

internal sealed record AgentUpdateStatus(
    int SchemaVersion,
    string State,
    string Code,
    string CurrentVersion,
    string? AvailableVersion,
    DateTimeOffset LastCheckUtc,
    string Message,
    string Trigger);

internal sealed record AgentUpdateAccessGrant(
    string CentralBaseUrl,
    string Ticket,
    DateTimeOffset ExpiresAtUtc);

internal sealed record AgentUpdateLatestResponse(
    string Version,
    string Runtime,
    string Channel,
    long Size,
    string Sha256,
    AgentReleaseDescriptor Descriptor,
    string DownloadTicket,
    DateTimeOffset DownloadTicketExpiresAtUtc);

internal sealed record AgentReleaseDescriptor(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("applicationId")] string ApplicationId,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("assetName")] string AssetName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("publishedAtUtc")] DateTimeOffset PublishedAtUtc,
    [property: JsonPropertyName("signature")] PolicySignature Signature);
