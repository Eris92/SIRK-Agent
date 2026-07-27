using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SirkAgent.Policy;

const string pipeName = "SIRK-Agent-Control";
var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "status";
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var agentRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");

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

if (command is "process" or "flush")
{
    try
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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
            : new Dictionary<string, object?> { ["telemetryEnabled"] = true, ["integrityMonitoring"] = true, ["testOnly"] = true },
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

Console.Error.WriteLine("Usage: sirkctl status|process|flush|queue-status|queue-clear-test --confirm-test-clear|verify-integrity|create-test-policy|create-test-recovery");
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

sealed record IntegrityManifest(IReadOnlyList<IntegrityManifestEntry> Files);
sealed record IntegrityManifestEntry(string Path, string Sha256);