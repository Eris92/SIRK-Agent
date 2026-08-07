using System.IO.Pipes;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SirkAgent.Policy;

const string pipeName = "SIRK-Agent-Control";
var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "status";
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var agentRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");

if (command == "create-test-update-manifest")
{
    var package = GetOption(args, "--package");
    var version = GetOption(args, "--version") ?? "0.1.1.999999";
    if (string.IsNullOrWhiteSpace(package) || !Directory.Exists(package))
        throw new DirectoryNotFoundException("Update package directory was not found.");
    package = Path.GetFullPath(package);
    var privateKeyPath = Path.Combine(agentRoot, "test-signing-key.pem");
    if (!File.Exists(privateKeyPath))
        throw new FileNotFoundException("Test signing key was not found. Run create-test-policy first.", privateKeyPath);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    key.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
    var files = Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories)
        .Where(path => !string.Equals(Path.GetFileName(path), "update-manifest.json", StringComparison.OrdinalIgnoreCase))
        .Select(path => new UpdateManifestFile(
            Path.GetRelativePath(package, path).Replace('\\', '/'),
            new FileInfo(path).Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .OrderBy(file => file.Path, StringComparer.Ordinal)
        .ToArray();
    var unsigned = new UpdateManifest(1, "sirk-agent", "SIRK Agent", version, "win-x64", files,
        new PolicySignature { Algorithm = "ES256", KeyId = "sirk-test-es256", Value = "pending" });
    var signature = key.SignData(CanonicalJson.SerializeWithoutTopLevelSignature(unsigned),
        HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    var manifest = unsigned with { Signature = unsigned.Signature with { Value = Base64Url(signature) } };
    var output = Path.Combine(package, "update-manifest.json");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(manifest, jsonOptions), new UTF8Encoding(false));
    Console.WriteLine(output);
    return;
}

if (command is "verify-update" or "stage-update")
{
    var package = GetOption(args, "--package");
    if (string.IsNullOrWhiteSpace(package) || !Directory.Exists(package))
        throw new DirectoryNotFoundException("Update package directory was not found.");
    package = Path.GetFullPath(package);
    var manifestPath = Path.Combine(package, "update-manifest.json");
    var trustedKeysPath = GetOption(args, "--trusted-keys") ?? Path.Combine(agentRoot, "trusted-keys.json");
    var verifier = new UpdatePackageVerifier(PemPolicyPublicKeyProvider.Load(trustedKeysPath));
    var result = verifier.Verify(package, manifestPath);
    if (!result.Accepted)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        Environment.ExitCode = 6;
        return;
    }
    if (command == "verify-update")
    {
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        return;
    }

    var manifest = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllBytesAsync(manifestPath),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Update manifest is invalid.");
    var installedService = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "SIRK Agent", "SirkAgent.Service.exe");
    if (File.Exists(installedService))
    {
        var installedVersion = FileVersionInfo.GetVersionInfo(installedService).ProductVersion;
        var versionGate = verifier.Verify(package, manifest, installedVersion);
        if (!versionGate.Accepted)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(versionGate, jsonOptions));
            Environment.ExitCode = 6;
            return;
        }
    }
    if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Version, @"^[0-9A-Za-z][0-9A-Za-z._+-]{0,63}$"))
        throw new InvalidDataException("Update version is invalid.");
    var updatesRoot = Path.Combine(agentRoot, "Updates", "Staged");
    var target = Path.Combine(updatesRoot, manifest.Version);
    if (Directory.Exists(target))
        throw new IOException("This update version is already staged.");
    var temporary = target + ".tmp." + Guid.NewGuid().ToString("N");
    try
    {
        foreach (var file in manifest.Files)
        {
            var destination = Path.Combine(temporary, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(package, file.Path.Replace('/', Path.DirectorySeparatorChar)), destination);
        }
        File.Copy(manifestPath, Path.Combine(temporary, "update-manifest.json"));
        Directory.CreateDirectory(updatesRoot);
        Directory.Move(temporary, target);
    }
    finally
    {
        if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        code = "UPDATE_STAGED",
        version = manifest.Version,
        stagedPath = target,
        verified = true
    }, jsonOptions));
    return;
}

if (command == "enroll")
{
    var endpointValue = GetOption(args, "--endpoint");
    var tokenFile = GetOption(args, "--bootstrap-token-file");
    if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var suppliedEndpoint) ||
        suppliedEndpoint.Scheme != Uri.UriSchemeHttps &&
        (suppliedEndpoint.Scheme != Uri.UriSchemeHttp || !suppliedEndpoint.IsLoopback))
        throw new ArgumentException("Enrollment endpoint must use HTTPS (HTTP is allowed only for loopback testing).");
    var endpoint = CanonicalAgentEndpoint(suppliedEndpoint, "/api/v1/agent/enroll");
    if (string.IsNullOrWhiteSpace(tokenFile) || !File.Exists(tokenFile))
        throw new FileNotFoundException("Bootstrap token file was not found.", tokenFile);

    var heartbeatPath = Path.Combine(agentRoot, "heartbeat-latest.json");
    using var heartbeat = JsonDocument.Parse(await File.ReadAllBytesAsync(heartbeatPath));
    var tenantId = heartbeat.RootElement.GetProperty("tenantId").GetString()
                   ?? throw new InvalidDataException("Heartbeat tenantId is empty.");
    var deviceId = heartbeat.RootElement.GetProperty("deviceId").GetString()
                   ?? throw new InvalidDataException("Heartbeat deviceId is empty.");
    var bootstrapToken = (await File.ReadAllTextAsync(tokenFile)).Trim();
    if (string.IsNullOrWhiteSpace(bootstrapToken))
        throw new InvalidDataException("Bootstrap token file is empty.");

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bootstrapToken);
    var credentialPath = Path.Combine(agentRoot, "portal-credential.bin");
    if (File.Exists(credentialPath))
        throw new InvalidOperationException("This device is already enrolled. Existing credentials were preserved.");
    var keyName = DeviceSigningKey.NameFor(tenantId, deviceId);
    if (DeviceSigningKey.Exists(keyName))
        throw new InvalidOperationException("A device signing key already exists. Existing key was preserved.");
    EnrollmentResponse enrollment;
    try
    {
        var publicKeySpki = Convert.ToBase64String(DeviceSigningKey.Create(keyName));
        using var response = await client.PostAsJsonAsync(endpoint, new
        {
            protocolVersion = 1,
            tenantId,
            deviceId,
            machineName = Environment.MachineName,
            publicKeySpki
        });
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        enrollment = JsonSerializer.Deserialize<EnrollmentResponse>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Enrollment response is invalid.");
    }
    catch
    {
        if (DeviceSigningKey.Exists(keyName))
            DeviceSigningKey.Delete(keyName);
        throw;
    }
    if (!enrollment.Ok || string.IsNullOrWhiteSpace(enrollment.DeviceToken) ||
        !string.Equals(enrollment.TenantId, tenantId, StringComparison.Ordinal) ||
        !string.Equals(enrollment.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Enrollment response does not match this device.");

    var checkInEndpoint = string.IsNullOrWhiteSpace(enrollment.CheckInEndpoint)
        ? CanonicalAgentEndpoint(endpoint, "/api/v1/agent/checkin")
        : CanonicalAgentEndpoint(new Uri(endpoint, enrollment.CheckInEndpoint),
            "/api/v1/agent/checkin");
    if (checkInEndpoint.Scheme != Uri.UriSchemeHttps &&
        (checkInEndpoint.Scheme != Uri.UriSchemeHttp || !checkInEndpoint.IsLoopback))
        throw new InvalidDataException("Enrollment returned an unsafe check-in endpoint.");
    new PortalCredentialStore(credentialPath,
        new DpapiMachineStateProtector()).Save(new PortalCredential(
        3, tenantId, deviceId, checkInEndpoint.AbsoluteUri,
        enrollment.DeviceToken, enrollment.EnrolledAtUtc ?? DateTimeOffset.UtcNow, null, keyName));
    var trustedPolicyKeysConfigured = false;
    if (enrollment.TrustedPolicyKeys is { Count: > 0 })
    {
        if (enrollment.TrustedPolicyKeys.Count > 10)
            throw new InvalidDataException("Enrollment returned too many trusted policy keys.");
        foreach (var trustedKey in enrollment.TrustedPolicyKeys)
        {
            if (string.IsNullOrWhiteSpace(trustedKey.KeyId) || trustedKey.KeyId.Length > 128 ||
                string.IsNullOrWhiteSpace(trustedKey.PublicKeyPem))
                throw new InvalidDataException("Enrollment returned an invalid trusted policy key.");
            using var policyKey = ECDsa.Create();
            policyKey.ImportFromPem(trustedKey.PublicKeyPem);
            if (policyKey.KeySize != 256)
                throw new InvalidDataException("Enrollment policy key must use ECDSA P-256.");
        }
        var trustedPath = Path.Combine(agentRoot, "trusted-keys.json");
        var temporaryTrustedPath = trustedPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporaryTrustedPath,
            JsonSerializer.Serialize(new { keys = enrollment.TrustedPolicyKeys },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporaryTrustedPath, trustedPath, overwrite: true);
        trustedPolicyKeysConfigured = true;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        tenantId,
        deviceId,
        endpoint = checkInEndpoint.AbsoluteUri,
        enrolledAtUtc = enrollment.EnrolledAtUtc,
        credentialProtected = true,
        signingKeyExportable = false,
        trustedPolicyKeysConfigured
    }, jsonOptions));
    return;
}

if (command == "rotate-device-key")
{
    var credentialPath = Path.Combine(agentRoot, "portal-credential.bin");
    var store = new PortalCredentialStore(credentialPath, new DpapiMachineStateProtector());
    var credential = store.Load() ?? throw new InvalidOperationException("This device is not enrolled.");
    if (!Uri.TryCreate(credential.Endpoint, UriKind.Absolute, out var checkInEndpoint))
        throw new InvalidDataException("Stored Portal endpoint is invalid.");
    var rotateEndpoint = new Uri(checkInEndpoint, "/api/v1/agent/rotate-key");
    var previousKeyName = credential.KeyName
                          ?? DeviceSigningKey.NameFor(credential.TenantId, credential.DeviceId);
    var keyName = previousKeyName + "-R" + Guid.NewGuid().ToString("N");
    if (DeviceSigningKey.Exists(keyName))
        throw new InvalidOperationException("The non-exportable replacement key already exists; no state was changed.");
    try
    {
        var publicKeySpki = Convert.ToBase64String(DeviceSigningKey.Create(keyName));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenantId = credential.TenantId,
            deviceId = credential.DeviceId,
            publicKeySpki
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var request = new HttpRequestMessage(HttpMethod.Post, rotateEndpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.DeviceToken);
        SignDeviceRequest(request, payload, credential);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        store.Save(credential with
        {
            SchemaVersion = 3,
            PrivateKeyPkcs8 = null,
            KeyName = keyName
        });
        if (!string.Equals(previousKeyName, keyName, StringComparison.Ordinal) &&
            DeviceSigningKey.Exists(previousKeyName))
        {
            DeviceSigningKey.Delete(previousKeyName);
        }
    }
    catch
    {
        if (DeviceSigningKey.Exists(keyName))
            DeviceSigningKey.Delete(keyName);
        throw;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        credentialSchema = 3,
        deviceIdentityPreserved = true,
        portalCredentialPreserved = true,
        signingKeyExportable = false
    }, jsonOptions));
    return;
}

if (command == "set-portal-endpoint")
{
    var endpointValue = GetOption(args, "--endpoint");
    if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != Uri.UriSchemeHttps &&
        (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback))
        throw new ArgumentException("Portal endpoint must use HTTPS (HTTP is allowed only for loopback testing).");
    var credentialPath = Path.Combine(agentRoot, "portal-credential.bin");
    var store = new PortalCredentialStore(credentialPath, new DpapiMachineStateProtector());
    var credential = store.Load() ?? throw new InvalidOperationException("This device is not enrolled.");
    var checkInEndpoint = new UriBuilder(endpoint)
    {
        Path = "/api/v1/agent/checkin",
        Query = string.Empty
    }.Uri;
    store.Save(credential with { Endpoint = checkInEndpoint.AbsoluteUri });
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        tenantId = credential.TenantId,
        deviceId = credential.DeviceId,
        endpoint = checkInEndpoint.AbsoluteUri,
        credentialRotated = false,
        deviceIdentityPreserved = true
    }, jsonOptions));
    return;
}

if (command == "verify-integrity")
{
    var manifestPath = Path.Combine(AppContext.BaseDirectory, "integrity-manifest.json");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine("INTEGRITY_MANIFEST_MISSING");
        Environment.ExitCode = 3;
        return;
    }

    var manifestOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var manifest = JsonSerializer.Deserialize<IntegrityManifest>(await File.ReadAllBytesAsync(manifestPath), manifestOptions);
    if (manifest?.Files is null || manifest.Files.Count == 0)
    {
        Console.Error.WriteLine("INTEGRITY_MANIFEST_INVALID");
        Environment.ExitCode = 3;
        return;
    }

    foreach (var entry in manifest.Files)
    {
        if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256))
        {
            Console.Error.WriteLine("INTEGRITY_MANIFEST_ENTRY_INVALID");
            Environment.ExitCode = 3;
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, entry.Path);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"INTEGRITY_FILE_MISSING: {entry.Path}");
            Environment.ExitCode = 3;
            return;
        }
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"INTEGRITY_HASH_MISMATCH: {entry.Path}");
            Environment.ExitCode = 3;
            return;
        }
    }
    Console.WriteLine("INTEGRITY_OK");
    return;
}

if (command == "status")
{
    var management = ReadJsonFile(Path.Combine(agentRoot, "management-state.json"));
    var heartbeat = ReadJsonFile(Path.Combine(agentRoot, "heartbeat-latest.json"));
    var security = ReadJsonFile(Path.Combine(agentRoot, "security-state.json"));
    var quarantine = ReadJsonFile(Path.Combine(agentRoot, "quarantine-status.json"));

    var response = new
    {
        ok = management is not null || heartbeat is not null || security is not null,
        machineName = Environment.MachineName,
        generatedAtUtc = DateTimeOffset.UtcNow,
        agentDataPath = agentRoot,
        management,
        heartbeat,
        security,
        quarantine
    };

    Console.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
    return;
}

if (command == "queue-status")
{
    var queuePath = Path.Combine(agentRoot, "TelemetryQueue");
    var files = Directory.Exists(queuePath)
        ? Directory.EnumerateFiles(queuePath, "*.bin", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.CreationTimeUtc)
            .ToArray()
        : Array.Empty<FileInfo>();
    var corrupt = Directory.Exists(queuePath)
        ? Directory.EnumerateFiles(queuePath, "*.corrupt.*", SearchOption.TopDirectoryOnly).Count()
        : 0;

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        path = queuePath,
        queuedFiles = files.Length,
        queuedBytes = files.Sum(file => file.Length),
        oldestUtc = files.FirstOrDefault()?.CreationTimeUtc,
        newestUtc = files.LastOrDefault()?.CreationTimeUtc,
        corruptFiles = corrupt,
        retention = new { maxBytes = 50L * 1024 * 1024, maxFiles = 5000, maxAgeDays = 14 }
    }, jsonOptions));
    return;
}

if (command == "queue-clear-test")
{
    if (!args.Skip(1).Any(value => string.Equals(value, "--confirm-test-clear", StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine("Refusing to clear telemetry queue. Add --confirm-test-clear for this test-only operation.");
        Environment.ExitCode = 2;
        return;
    }

    var queuePath = Path.Combine(agentRoot, "TelemetryQueue");
    var deleted = 0;
    var failed = 0;
    if (Directory.Exists(queuePath))
    {
        foreach (var path in Directory.EnumerateFiles(queuePath, "*.bin", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(path); deleted++; }
            catch { failed++; }
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = failed == 0,
        testOnly = true,
        path = queuePath,
        deletedFiles = deleted,
        failedFiles = failed
    }, jsonOptions));
    if (failed > 0) Environment.ExitCode = 5;
    return;
}

if (command is "process" or "flush" or "sync")
{
    try
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await writer.WriteLineAsync(command);
        var response = await ReadSingleJsonAsync(reader, timeout.Token);
        using var document = JsonDocument.Parse(response);
        Console.WriteLine(JsonSerializer.Serialize(document.RootElement, jsonOptions));
        return;
    }
    catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException or JsonException)
    {
        var fallback = await RunControlFileFallbackAsync(command);
        using var document = JsonDocument.Parse(fallback);
        Console.WriteLine(JsonSerializer.Serialize(document.RootElement, jsonOptions));
        return;
    }
}

if (command is "create-test-policy" or "create-test-recovery")
{
    var heartbeatPath = Path.Combine(agentRoot, "heartbeat-latest.json");
    if (!File.Exists(heartbeatPath))
        throw new FileNotFoundException("Heartbeat not found. Start the agent first.", heartbeatPath);

    using var heartbeat = JsonDocument.Parse(await File.ReadAllBytesAsync(heartbeatPath));
    var deviceId = heartbeat.RootElement.GetProperty("deviceId").GetString()
                   ?? throw new InvalidDataException("Heartbeat deviceId is empty.");
    Directory.CreateDirectory(Path.Combine(agentRoot, "Incoming"));

    var privateKeyPath = Path.Combine(agentRoot, "test-signing-key.pem");
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    if (File.Exists(privateKeyPath))
        key.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
    else
        await File.WriteAllTextAsync(privateKeyPath, key.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));

    var keyId = "sirk-test-es256";
    var trusted = new
    {
        keys = new[] { new { keyId, publicKeyPem = key.ExportSubjectPublicKeyInfoPem() } }
    };
    await File.WriteAllTextAsync(Path.Combine(agentRoot, "trusted-keys.json"),
        JsonSerializer.Serialize(trusted, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
        new UTF8Encoding(false));

    var now = DateTimeOffset.UtcNow;
    var policyId = command == "create-test-recovery" ? $"test-recovery-{now:yyyyMMddHHmmss}" : $"test-policy-{now:yyyyMMddHHmmss}";
    var envelope = new PolicyEnvelope
    {
        TenantId = "investa",
        DeviceId = deviceId,
        PolicyId = policyId,
        CaseId = command == "create-test-recovery" ? "TEST-RECOVERY" : null,
        Version = now.ToUnixTimeSeconds(),
        Epoch = 1,
        NotBeforeUtc = now.AddMinutes(-1),
        ExpiresAtUtc = now.AddDays(7),
        Nonce = Guid.NewGuid().ToString("N"),
        Mode = command == "create-test-recovery" ? AgentMode.Emergency : AgentMode.Normal,
        Settings = command == "create-test-recovery"
            ? new Dictionary<string, object?> { ["recoveryAction"] = "clearQuarantine", ["testOnly"] = true }
            : new Dictionary<string, object?>
            {
                ["telemetryEnabled"] = true,
                ["integrityMonitoring"] = true,
                ["remoteTerminalEnabled"] = true,
                ["remoteFilesEnabled"] = true,
                ["remoteDesktopEnabled"] = true,
                ["testOnly"] = true
            },
        Signature = new PolicySignature { Algorithm = "ES256", KeyId = keyId, Value = "pending" }
    };
    var signature = key.SignData(CanonicalJson.SerializePayloadWithoutSignature(envelope), HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    envelope = envelope with { Signature = envelope.Signature with { Value = Base64Url(signature) } };

    var output = Path.Combine(agentRoot, "Incoming", $"{policyId}.policy.json");
    await File.WriteAllTextAsync(output,
        JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
        new UTF8Encoding(false));
    Console.WriteLine(output);
    return;
}

Console.Error.WriteLine("Usage: sirkctl create-test-update-manifest --package <directory> --version <version>|verify-update|stage-update --package <directory> [--trusted-keys <path>]|enroll --endpoint <url> --bootstrap-token-file <path>|rotate-device-key|set-portal-endpoint --endpoint <checkin-url>|status|process|flush|sync|queue-status|queue-clear-test --confirm-test-clear|verify-integrity|create-test-policy|create-test-recovery");
Environment.ExitCode = 2;

static JsonElement? ReadJsonFile(string path)
{
    if (!File.Exists(path))
        return null;

    try
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }
    catch (JsonException)
    {
        return null;
    }
    catch (IOException)
    {
        return null;
    }
}

static async Task<string> ReadSingleJsonAsync(StreamReader reader, CancellationToken token)
{
    var builder = new StringBuilder();
    var buffer = new char[1];
    var depth = 0;
    var started = false;
    var inString = false;
    var escaped = false;

    while (await reader.ReadAsync(buffer.AsMemory(0, 1), token) > 0)
    {
        var value = buffer[0];
        builder.Append(value);

        if (!started)
        {
            if (char.IsWhiteSpace(value))
                continue;
            if (value is not ('{' or '['))
                throw new JsonException("Control response does not start with a JSON object or array.");
            started = true;
            depth = 1;
            continue;
        }

        if (inString)
        {
            if (escaped)
            {
                escaped = false;
            }
            else if (value == '\\')
            {
                escaped = true;
            }
            else if (value == '"')
            {
                inString = false;
            }
            continue;
        }

        if (value == '"')
        {
            inString = true;
            continue;
        }

        if (value is '{' or '[')
            depth++;
        else if (value is '}' or ']')
            depth--;

        if (started && depth == 0)
            return builder.ToString().Trim();
    }

    throw new EndOfStreamException("Control response ended before a complete JSON document was received.");
}

static async Task<string> RunControlFileFallbackAsync(string command)
{
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
    Directory.CreateDirectory(root);
    var requestPath = Path.Combine(root, "control-request.json");
    var responsePath = Path.Combine(root, "control-response.json");
    var requestId = Guid.NewGuid();
    if (File.Exists(responsePath)) File.Delete(responsePath);
    var request = new { requestId, timestampUtc = DateTimeOffset.UtcNow, command };
    await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(false));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (File.Exists(responsePath))
        {
            var response = await File.ReadAllTextAsync(responsePath);
            if (response.Contains(requestId.ToString(), StringComparison.OrdinalIgnoreCase))
                return response;
        }
        await Task.Delay(250);
    }
    throw new TimeoutException("SIRK Agent did not answer the local control request.");
}

static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static void SignDeviceRequest(HttpRequestMessage request, byte[] payload, PortalCredential credential)
{
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
        System.Globalization.CultureInfo.InvariantCulture);
    var nonce = Guid.NewGuid().ToString("N");
    var prefix = Encoding.UTF8.GetBytes(timestamp + "\n" + nonce + "\n");
    var signed = new byte[prefix.Length + payload.Length];
    Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
    Buffer.BlockCopy(payload, 0, signed, prefix.Length, payload.Length);
    byte[] signature;
    if (!string.IsNullOrWhiteSpace(credential.KeyName))
        signature = DeviceSigningKey.Sign(credential.KeyName, signed);
    else if (!string.IsNullOrWhiteSpace(credential.PrivateKeyPkcs8))
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(credential.PrivateKeyPkcs8), out _);
        signature = key.SignData(signed, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
    else throw new InvalidDataException("Portal credential does not contain a signing key.");
    request.Headers.Add("X-SIRK-Timestamp", timestamp);
    request.Headers.Add("X-SIRK-Nonce", nonce);
    request.Headers.Add("X-SIRK-Signature", Convert.ToBase64String(signature));
}

static Uri CanonicalAgentEndpoint(Uri source, string path) =>
    new UriBuilder(source) { Path = path, Query = string.Empty }.Uri;

static string? GetOption(string[] values, string name)
{
    var index = Array.FindIndex(values, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

sealed record IntegrityManifest(IReadOnlyList<IntegrityManifestEntry> Files);
sealed record IntegrityManifestEntry(string Path, string Sha256);
sealed record EnrollmentResponse(bool Ok, string TenantId, string DeviceId, string DeviceToken,
    string? CheckInEndpoint, DateTimeOffset? EnrolledAtUtc,
    IReadOnlyList<EnrollmentTrustedPolicyKey>? TrustedPolicyKeys);
sealed record EnrollmentTrustedPolicyKey(string KeyId, string PublicKeyPem);
