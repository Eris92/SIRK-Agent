using System.IO.Pipes;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed class ManagementWorker : BackgroundService
{
    private const string TenantId = "investa";
    private const string PipeName = "SIRK-Agent-Control";
    private readonly ILogger<ManagementWorker> _logger;
    private readonly PortalReconnectSignal _reconnectSignal;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private int _portalSessionGeneration;

    public ManagementWorker(ILogger<ManagementWorker> logger, PortalReconnectSignal reconnectSignal)
    {
        _logger = logger;
        _reconnectSignal = reconnectSignal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var paths = ManagementPaths.CreateDefault();
        paths.EnsureDirectories();
        var protector = new DpapiMachineStateProtector();
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath, protector).LoadOrCreate(TenantId);
        var queue = new TelemetryQueue(paths.TelemetryQueueDirectory, protector, 50L * 1024 * 1024, _json);

        await WriteStateAsync(paths, new ManagementState(DateTimeOffset.UtcNow, "Starting", "MANAGEMENT_STARTING",
            null, null, 0, 0, false, null), stoppingToken);

        var pipeTask = RunPipeServerAsync(paths, queue, stoppingToken);
        var portalSessionStartedUtc = DateTime.UtcNow;
        var portalTask = StartPortalSession(paths, queue, identity.DeviceId, stoppingToken);
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            await ProcessInboxAsync(paths, identity.DeviceId, stoppingToken);
            await ValidateIntegrityAsync(paths, stoppingToken);
            await FlushTelemetryAsync(paths, queue, stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessInboxAsync(paths, identity.DeviceId, stoppingToken);
                await ValidateIntegrityAsync(paths, stoppingToken);
                await FlushTelemetryAsync(paths, queue, stoppingToken);
                var lastPortalActivity = File.Exists(paths.PortalStatusPath)
                    ? File.GetLastWriteTimeUtc(paths.PortalStatusPath)
                    : portalSessionStartedUtc;
                if (DateTime.UtcNow - (lastPortalActivity > portalSessionStartedUtc
                        ? lastPortalActivity : portalSessionStartedUtc) > TimeSpan.FromSeconds(12))
                {
                    portalSessionStartedUtc = DateTime.UtcNow;
                    portalTask = StartPortalSession(paths, queue, identity.DeviceId, stoppingToken);
                    WritePortalLoopDiagnostic(paths, "supervisor-restart");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            timer.Dispose();
            try { await pipeTask; } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            try { await portalTask; } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private Task StartPortalSession(ManagementPaths paths, TelemetryQueue queue, string deviceId,
        CancellationToken token)
    {
        var generation = Interlocked.Increment(ref _portalSessionGeneration);
        return RunPortalSessionAsync(paths, queue, deviceId, generation, token);
    }

    private async Task RunPortalSessionAsync(ManagementPaths paths, TelemetryQueue queue, string deviceId,
        int generation, CancellationToken token)
    {
        while (!token.IsCancellationRequested && generation == Volatile.Read(ref _portalSessionGeneration))
        {
            try
            {
                var observedNetwork = _reconnectSignal.Generation;
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var networkWait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var checkIn = CheckInPortalAsync(paths, queue, deviceId, cycle.Token);
                var networkChange = _reconnectSignal.WaitForChangeAsync(observedNetwork, networkWait.Token);
                var completed = await Task.WhenAny(checkIn, networkChange);
                if (completed == networkChange)
                {
                    cycle.Cancel();
                    try { await checkIn; }
                    catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
                    WritePortalLoopDiagnostic(paths, "network-change-reconnect");
                    continue;
                }
                networkWait.Cancel();
                try { await networkChange; }
                catch (OperationCanceledException) when (networkWait.IsCancellationRequested) { }
                await checkIn;
                var status = ReadJson(paths.PortalStatusPath);
                var connected = status is { ValueKind: JsonValueKind.Object } &&
                                status.Value.TryGetProperty("ok", out var ok) &&
                                ok.ValueKind == JsonValueKind.True;
                if (!connected) await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Persistent Portal session failed; reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
    }

    private async Task ProcessInboxAsync(ManagementPaths paths, string deviceId, CancellationToken token)
    {
        if (!await _processLock.WaitAsync(0, token))
            return;

        try
        {
            var keyProvider = JsonPolicyKeyProvider.Load(paths.TrustedKeysPath, _json);
            var validator = new PolicyValidator(keyProvider);
            var protector = new DpapiMachineStateProtector();
            var store = new FilePolicyStateStore(paths.PolicyStatePath, protector);
            var state = new PolicyStateHealthChecker(paths.PolicyStatePath, store).Check().State ?? PolicyState.Empty;
            var accepted = 0;
            var rejected = 0;
            string? lastPolicyId = null;
            string? lastError = null;

            foreach (var file in Directory.EnumerateFiles(paths.InboxDirectory, "*.policy.json")
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<PolicyEnvelope>(await File.ReadAllBytesAsync(file, token), _json)
                        ?? throw new InvalidDataException("Policy envelope deserialized to null.");
                    var context = new PolicyValidationContext(TenantId, deviceId, state.Epoch, state.Version,
                        DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), state.SeenNonces.ToHashSet(StringComparer.Ordinal));
                    var validation = validator.Validate(envelope, context);
                    if (!validation.IsValid)
                        throw new PolicyRejectedException(validation.Code, validation.Message);

                    var hash = Convert.ToHexString(SHA256.HashData(CanonicalJson.SerializePayloadWithoutSignature(envelope)));
                    var nonces = state.SeenNonces.Concat(new[] { envelope.Nonce }).TakeLast(128).ToArray();
                    state = new PolicyState
                    {
                        Epoch = envelope.Epoch,
                        Version = envelope.Version,
                        ActivePolicyHash = hash,
                        ActivePolicyId = envelope.PolicyId,
                        ActiveCaseId = envelope.CaseId,
                        AcceptedAtUtc = DateTimeOffset.UtcNow,
                        SeenNonces = nonces
                    };
                    store.Save(state);
                    AtomicFile.WriteJson(paths.ActivePolicyPath, envelope, _json);
                    lastPolicyId = envelope.PolicyId;
                    accepted++;

                    if (envelope.Mode == AgentMode.Emergency && TryGetSetting(envelope, "recoveryAction", out var action) &&
                        string.Equals(action, "clearQuarantine", StringComparison.OrdinalIgnoreCase))
                    {
                        ArchiveIfExists(paths.QuarantineProtectedPath, paths.RecoveryArchiveDirectory);
                        ArchiveIfExists(paths.QuarantineStatusPath, paths.RecoveryArchiveDirectory);
                        ArchiveIfExists(paths.TamperEventPath, paths.RecoveryArchiveDirectory);
                    }

                    MoveWithResult(file, paths.AcceptedDirectory, "accepted");
                }
                catch (Exception ex)
                {
                    rejected++;
                    lastError = ex.Message;
                    MoveWithResult(file, paths.RejectedDirectory, "rejected");
                    await File.WriteAllTextAsync(Path.Combine(paths.RejectedDirectory,
                        $"{Path.GetFileName(file)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.error.txt"), ex.ToString(), token);
                }
            }

            var current = await ReadStateAsync(paths, token);
            await WriteStateAsync(paths, current with
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Status = rejected > 0 ? "Warning" : "Healthy",
                Code = rejected > 0 ? "POLICY_REJECTED" : accepted > 0 ? "POLICY_ACCEPTED" : "POLICY_INBOX_IDLE",
                LastPolicyId = lastPolicyId ?? current.LastPolicyId,
                LastError = lastError,
                AcceptedPolicies = current.AcceptedPolicies + accepted,
                RejectedPolicies = current.RejectedPolicies + rejected
            }, token);
        }
        finally
        {
            _processLock.Release();
        }
    }

    private async Task ValidateIntegrityAsync(ManagementPaths paths, CancellationToken token)
    {
        var current = await ReadStateAsync(paths, token);
        if (!File.Exists(paths.IntegrityManifestPath))
        {
            await WriteStateAsync(paths, current with
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                IntegrityVerified = false,
                IntegrityCode = "INTEGRITY_MANIFEST_MISSING"
            }, token);
            return;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<IntegrityManifest>(await File.ReadAllBytesAsync(paths.IntegrityManifestPath, token), _json)
                ?? throw new InvalidDataException("Integrity manifest deserialized to null.");
            foreach (var entry in manifest.Files)
            {
                var path = Path.Combine(AppContext.BaseDirectory, entry.Path);
                if (!File.Exists(path))
                    throw new InvalidDataException($"Missing protected file: {entry.Path}");
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(path), token));
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Hash mismatch: {entry.Path}");
            }

            await WriteStateAsync(paths, current with
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                IntegrityVerified = true,
                IntegrityCode = "INTEGRITY_OK"
            }, token);
        }
        catch (Exception ex)
        {
            await WriteStateAsync(paths, current with
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Status = "Critical",
                Code = "INTEGRITY_FAILED",
                IntegrityVerified = false,
                IntegrityCode = ex.Message,
                LastError = ex.ToString()
            }, token);
        }
    }

    private async Task FlushTelemetryAsync(ManagementPaths paths, TelemetryQueue queue, CancellationToken token)
    {
        if (!File.Exists(paths.ManagementConfigPath))
            return;
        var config = JsonSerializer.Deserialize<ManagementConfig>(await File.ReadAllBytesAsync(paths.ManagementConfigPath, token), _json);
        if (config is null || !config.Enabled || string.IsNullOrWhiteSpace(config.TelemetryEndpoint))
            return;

        var items = queue.ReadReady(Math.Clamp(config.BatchSize, 1, 100), DateTimeOffset.UtcNow);
        if (items.Count == 0)
            return;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 5, 120)) };
        if (!string.IsNullOrWhiteSpace(config.BearerToken))
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.BearerToken);

        try
        {
            using var response = await client.PostAsJsonAsync(config.TelemetryEndpoint,
                new { device = Environment.MachineName, events = items.Select(x => x.Envelope).ToArray() }, _json, token);
            response.EnsureSuccessStatusCode();
            foreach (var item in items)
                queue.Complete(item);
        }
        catch (Exception ex)
        {
            var delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(10, items.Max(x => x.Envelope.Attempt) + 1)) * 15));
            foreach (var item in items)
                queue.Retry(item, DateTimeOffset.UtcNow + delay);
            _logger.LogWarning(ex, "Telemetry delivery failed; retry scheduled after {Delay}.", delay);
        }
    }

    private async Task CheckInPortalAsync(ManagementPaths paths, TelemetryQueue queue, string deviceId,
        CancellationToken token)
    {
        ManagementConfig? config = null;
        if (File.Exists(paths.ManagementConfigPath))
            config = JsonSerializer.Deserialize<ManagementConfig>(
                await File.ReadAllBytesAsync(paths.ManagementConfigPath, token), _json);

        PortalCredential? credential = null;
        try
        {
            credential = new PortalCredentialStore(paths.PortalCredentialPath,
                new DpapiMachineStateProtector()).Load();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected Portal credential could not be loaded.");
            return;
        }

        if (credential is not null &&
            (!string.Equals(credential.TenantId, TenantId, StringComparison.Ordinal) ||
             !string.Equals(credential.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Protected Portal credential does not match this device.");
            return;
        }

        var endpointValue = credential?.Endpoint ??
                            (config?.PortalEnabled == true ? config.PortalEndpoint : null);
        var tokenValue = credential?.DeviceToken ??
                         (config?.PortalEnabled == true ? config.DeviceToken : null);
        if (string.IsNullOrWhiteSpace(endpointValue) || string.IsNullOrWhiteSpace(tokenValue))
            return;
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var configuredEndpoint) ||
            configuredEndpoint.Scheme != Uri.UriSchemeHttps &&
            (configuredEndpoint.Scheme != Uri.UriSchemeHttp || !configuredEndpoint.IsLoopback))
        {
            _logger.LogWarning("Portal check-in endpoint is invalid.");
            return;
        }
        var endpoint = CanonicalAgentEndpoint(configuredEndpoint, "/api/v1/agent/checkin");

        var items = queue.ReadReady(Math.Clamp(config?.BatchSize ?? 25, 1, 100), DateTimeOffset.UtcNow);
        var payload = new
        {
            protocolVersion = 1,
            tenantId = TenantId,
            deviceId,
            machineName = Environment.MachineName,
            agentVersion = typeof(ManagementWorker).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? typeof(ManagementWorker).Assembly.GetName().Version?.ToString(),
            heartbeat = ReadJson(paths.HeartbeatPath),
            management = ReadJson(paths.ManagementStatePath),
            runtimeHealth = ReadJson(Path.Combine(paths.Root, "runtime-health.json")),
            watchdog = ReadJson(Path.Combine(paths.Root, "Watchdog", "watchdog-status.json")),
            network = ReadJson(Path.Combine(paths.Root, "network-status.json")),
            security = ReadJson(Path.Combine(paths.Root, "security-state.json")),
            quarantine = ReadJson(Path.Combine(paths.Root, "quarantine-status.json")),
            endurance = ReadJson(Path.Combine(paths.Root, "endurance-summary.json")),
            activity = ReadJson(Path.Combine(paths.Root, "activity-latest.json")),
            browserActivity = ReadJson(Path.Combine(paths.Root, "browser-activity-latest.json")),
            risk = ReadJson(Path.Combine(paths.Root, "risk-report.json")),
            tamper = ReadJson(Path.Combine(paths.Root, "tamper-event-latest.json")),
            portalStatus = ReadJson(paths.PortalStatusPath),
            telemetryQueue = queue.Snapshot(),
            acknowledgedPolicyIds = ReadAcknowledgedPolicyIds(paths.ActivePolicyPath),
            commandResults = ReadCommandResults(paths.CommandResultsDirectory),
            waitMilliseconds = 5000,
            events = items.Select(x => x.Envelope).ToArray()
        };

        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var timeoutSeconds = Math.Clamp(config?.TimeoutSeconds ?? 15, 8, 30);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var portalClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, _json);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(payloadBytes)
            };
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenValue);
            if (credential is not null)
                SignPortalRequest(request, payloadBytes, credential);
            PortalCheckInResponse portalResponse;
            var sendTask = Task.Factory.StartNew(
                    () => portalClient.SendAsync(request, requestTimeout.Token),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .Unwrap();
            var watchdog = Task.Delay(TimeSpan.FromSeconds(12), token);
            if (await Task.WhenAny(sendTask, watchdog) != sendTask)
            {
                portalClient.CancelPendingRequests();
                _ = sendTask.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                throw new TimeoutException("Portal check-in transport did not complete within 12 seconds.");
            }
            using (var response = await sendTask)
            {
                response.EnsureSuccessStatusCode();
                portalResponse = await response.Content.ReadFromJsonAsync<PortalCheckInResponse>(
                                     _json, requestTimeout.Token)
                                 ?? throw new InvalidDataException("Portal check-in response is empty.");
            }
            if (!portalResponse.Ok)
                throw new InvalidDataException("Portal rejected the check-in.");
            SynchronizeTrustedPolicyKeys(paths.TrustedKeysPath, portalResponse.TrustedPolicyKeys);
            new PortalPolicyDeliveryStore(paths.InboxDirectory, _json)
                .Store(TenantId, deviceId, portalResponse.Policies);
            if (credential is not null &&
                !string.Equals(credential.Endpoint, endpoint.AbsoluteUri, StringComparison.Ordinal))
            {
                new PortalCredentialStore(paths.PortalCredentialPath,
                    new DpapiMachineStateProtector()).Save(
                    credential with { Endpoint = endpoint.AbsoluteUri });
            }
            DeleteCommandResults(paths.CommandResultsDirectory);
            _ = await ExecuteRemoteCommandsAsync(paths, portalResponse.Commands, token);
            foreach (var item in items)
                queue.Complete(item);
            AtomicFile.WriteJson(paths.PortalStatusPath, new
            {
                ok = true,
                checkedInAtUtc = DateTimeOffset.UtcNow,
                endpoint = endpoint.GetLeftPart(UriPartial.Authority),
                deliveredCommands = portalResponse.Commands?.Count ?? 0
            }, _json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Portal check-in failed.");
            AtomicFile.WriteJson(paths.PortalStatusPath, new
            {
                ok = false,
                failedAtUtc = DateTimeOffset.UtcNow,
                endpoint = endpoint.GetLeftPart(UriPartial.Authority),
                error = ex.GetType().Name,
                message = ex.Message
            }, _json);
        }
    }


    private void SynchronizeTrustedPolicyKeys(
        string path,
        IReadOnlyList<TrustedKeyEntry>? supplied)
    {
        if (supplied is not { Count: > 0 }) return;
        if (supplied.Count > 10)
            throw new InvalidDataException("Portal returned too many trusted policy keys.");

        var normalized = supplied.Select(ValidateTrustedPolicyKey)
            .OrderBy(value => value.KeyId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(value => value.KeyId).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new InvalidDataException("Portal returned duplicate trusted policy key identifiers.");

        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<TrustedKeyDocument>(File.ReadAllBytes(path), _json)
                           ?? new TrustedKeyDocument([]);
            var current = existing.Keys.Select(ValidateTrustedPolicyKey)
                .OrderBy(value => value.KeyId, StringComparer.Ordinal)
                .ToArray();
            if (current.Length == 0)
            {
                AtomicFile.WriteJson(path, new TrustedKeyDocument(normalized), _json);
                return;
            }
            if (current.Length != normalized.Length ||
                current.Where((value, index) =>
                        !string.Equals(value.KeyId, normalized[index].KeyId, StringComparison.Ordinal) ||
                        !PublicKeysEqual(value.PublicKeyPem, normalized[index].PublicKeyPem))
                    .Any())
            {
                throw new InvalidDataException(
                    "Portal attempted to replace an established trusted policy key set.");
            }
            return;
        }

        AtomicFile.WriteJson(path, new TrustedKeyDocument(normalized), _json);
    }

    private static TrustedKeyEntry ValidateTrustedPolicyKey(TrustedKeyEntry value)
    {
        if (string.IsNullOrWhiteSpace(value.KeyId) || value.KeyId.Length > 128 ||
            string.IsNullOrWhiteSpace(value.PublicKeyPem))
            throw new InvalidDataException("Portal returned an invalid trusted policy key.");
        using var key = ECDsa.Create();
        key.ImportFromPem(value.PublicKeyPem);
        if (key.KeySize != 256)
            throw new InvalidDataException("Trusted policy key must use ECDSA P-256.");
        return new TrustedKeyEntry(value.KeyId.Trim(), key.ExportSubjectPublicKeyInfoPem());
    }

    private static bool PublicKeysEqual(string left, string right)
    {
        using var leftKey = ECDsa.Create();
        using var rightKey = ECDsa.Create();
        leftKey.ImportFromPem(left);
        rightKey.ImportFromPem(right);
        var leftBytes = leftKey.ExportSubjectPublicKeyInfo();
        var rightBytes = rightKey.ExportSubjectPublicKeyInfo();
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static Uri CanonicalAgentEndpoint(Uri source, string path) =>
        new UriBuilder(source) { Path = path, Query = string.Empty }.Uri;

    private void WritePortalLoopDiagnostic(ManagementPaths paths, string stage) =>
        AtomicFile.WriteJson(Path.Combine(paths.Root, "portal-loop-diagnostic.json"),
            new { timestampUtc = DateTimeOffset.UtcNow, stage }, _json);

    private async Task<int> ExecuteRemoteCommandsAsync(ManagementPaths paths,
        IReadOnlyList<PortalRemoteCommand>? commands, CancellationToken token)
    {
        if (commands is not { Count: > 0 }) return 0;
        Directory.CreateDirectory(paths.CommandResultsDirectory);
        var policy = ReadJson(paths.ActivePolicyPath);
        var executor = new RemoteCommandExecutor(_json);
        foreach (var command in commands.Take(5))
        {
            var result = await executor.ExecuteAsync(command, policy, token);
            AtomicFile.WriteJson(Path.Combine(paths.CommandResultsDirectory,
                command.CommandId + ".result.json"), result, _json);
        }
        return Math.Min(5, commands.Count);
    }

    private static JsonElement[] ReadCommandResults(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.result.json").OrderBy(x => x)
            .Take(10).Select(ReadJson).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
    }

    private static void DeleteCommandResults(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.result.json").Take(10))
            File.Delete(file);
    }

    private static void SignPortalRequest(HttpRequestMessage request, byte[] payload, PortalCredential credential)
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
        {
            signature = DeviceSigningKey.Sign(credential.KeyName, signed);
        }
        else if (!string.IsNullOrWhiteSpace(credential.PrivateKeyPkcs8))
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(credential.PrivateKeyPkcs8), out _);
            signature = key.SignData(signed, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        else
        {
            throw new InvalidDataException("Portal credential does not contain a device signing key.");
        }
        request.Headers.Add("X-SIRK-Timestamp", timestamp);
        request.Headers.Add("X-SIRK-Nonce", nonce);
        request.Headers.Add("X-SIRK-Signature", Convert.ToBase64String(signature));
    }

    private async Task RunPipeServerAsync(ManagementPaths paths, TelemetryQueue queue, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = CreateControlPipe();
            await pipe.WaitForConnectionAsync(token);
            if (!IsAuthorizedPipeClient(pipe))
            {
                _logger.LogWarning("Rejected an unauthorized local control pipe client.");
                pipe.Disconnect();
                continue;
            }
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var command = (await reader.ReadLineAsync(token))?.Trim().ToLowerInvariant();
            object response = command switch
            {
                "status" => new { ok = true, management = await ReadStateAsync(paths, token), heartbeat = ReadJson(paths.HeartbeatPath) },
                "process" => await ProcessCommandAsync(paths, token),
                "flush" => await FlushCommandAsync(paths, queue, token),
                "sync" => await SyncCommandAsync(paths, queue, token),
                _ => new { ok = false, error = "Supported commands: status, process, flush, sync." }
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, _json));
        }
    }

    private async Task<object> ProcessCommandAsync(ManagementPaths paths, CancellationToken token)
    {
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath, new DpapiMachineStateProtector()).LoadOrCreate(TenantId);
        await ProcessInboxAsync(paths, identity.DeviceId, token);
        return new { ok = true, management = await ReadStateAsync(paths, token) };
    }

    private async Task<object> FlushCommandAsync(ManagementPaths paths, TelemetryQueue queue, CancellationToken token)
    {
        await FlushTelemetryAsync(paths, queue, token);
        return new { ok = true, queuedFiles = queue.SnapshotFiles().Count, queuedBytes = queue.TotalBytes() };
    }

    private static NamedPipeServerStream CreateControlPipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private static bool IsAuthorizedPipeClient(NamedPipeServerStream pipe)
    {
        var authorized = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            authorized = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true ||
                         new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        });
        return authorized;
    }

    private async Task<object> SyncCommandAsync(ManagementPaths paths, TelemetryQueue queue, CancellationToken token)
    {
        var identity = new DeviceIdentityStore(paths.DeviceIdentityPath,
            new DpapiMachineStateProtector()).LoadOrCreate(TenantId);
        await CheckInPortalAsync(paths, queue, identity.DeviceId, token);
        return new { ok = true, requestedAtUtc = DateTimeOffset.UtcNow };
    }

    private async Task<ManagementState> ReadStateAsync(ManagementPaths paths, CancellationToken token)
    {
        if (!File.Exists(paths.ManagementStatePath))
            return new ManagementState(DateTimeOffset.UtcNow, "Starting", "MANAGEMENT_STARTING", null, null, 0, 0, false, null);
        return JsonSerializer.Deserialize<ManagementState>(await File.ReadAllBytesAsync(paths.ManagementStatePath, token), _json)
               ?? new ManagementState(DateTimeOffset.UtcNow, "Warning", "MANAGEMENT_STATE_INVALID", null, null, 0, 0, false, null);
    }

    private Task WriteStateAsync(ManagementPaths paths, ManagementState state, CancellationToken token)
    {
        AtomicFile.WriteJson(paths.ManagementStatePath, state, _json);
        return Task.CompletedTask;
    }

    private static JsonElement? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.Clone();
    }

    private static string[] ReadAcknowledgedPolicyIds(string activePolicyPath)
    {
        var active = ReadJson(activePolicyPath);
        if (active is not { ValueKind: JsonValueKind.Object } ||
            !active.Value.TryGetProperty("policyId", out var policyId) ||
            policyId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(policyId.GetString()))
            return [];
        return [policyId.GetString()!];
    }

    private static bool TryGetSetting(PolicyEnvelope envelope, string name, out string? value)
    {
        value = null;
        if (!envelope.Settings.TryGetValue(name, out var raw) || raw is null) return false;
        if (raw is JsonElement element)
        {
            value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return true;
        }
        value = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static void ArchiveIfExists(string path, string archiveDirectory)
    {
        if (!File.Exists(path)) return;
        Directory.CreateDirectory(archiveDirectory);
        File.Move(path, Path.Combine(archiveDirectory,
            $"{Path.GetFileName(path)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak"), overwrite: false);
    }

    private static void MoveWithResult(string source, string directory, string suffix)
    {
        Directory.CreateDirectory(directory);
        File.Move(source, Path.Combine(directory,
            $"{Path.GetFileNameWithoutExtension(source)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{suffix}.json"), overwrite: false);
    }

    private sealed class PolicyRejectedException : Exception
    {
        public PolicyRejectedException(string code, string message) : base($"{code}: {message}") { }
    }
}

internal sealed record ManagementState(DateTimeOffset TimestampUtc, string Status, string Code,
    string? LastPolicyId, string? LastError, long AcceptedPolicies, long RejectedPolicies,
    bool IntegrityVerified, string? IntegrityCode);

internal sealed record ManagementConfig(bool Enabled, string? TelemetryEndpoint, string? BearerToken,
    int BatchSize = 25, int TimeoutSeconds = 30, bool PortalEnabled = false,
    string? PortalEndpoint = null, string? DeviceToken = null);
internal sealed record IntegrityManifest(IReadOnlyList<IntegrityManifestEntry> Files);
internal sealed record IntegrityManifestEntry(string Path, string Sha256);
internal sealed record PortalCheckInResponse(
    bool Ok,
    IReadOnlyList<TrustedKeyEntry>? TrustedPolicyKeys,
    IReadOnlyList<JsonElement>? Policies,
    IReadOnlyList<PortalRemoteCommand>? Commands);

internal sealed record ManagementPaths(string Root, string InboxDirectory, string AcceptedDirectory,
    string RejectedDirectory, string RecoveryArchiveDirectory, string TrustedKeysPath,
    string ManagementConfigPath, string ManagementStatePath, string ActivePolicyPath,
    string IntegrityManifestPath, string PolicyStatePath, string DeviceIdentityPath, string PortalCredentialPath,
    string PortalStatusPath,
    string TelemetryQueueDirectory, string CommandResultsDirectory, string HeartbeatPath, string QuarantineProtectedPath,
    string QuarantineStatusPath, string TamperEventPath)
{
    public static ManagementPaths CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
        return new ManagementPaths(root, Path.Combine(root, "Incoming"), Path.Combine(root, "Archive", "Accepted"),
            Path.Combine(root, "Archive", "Rejected"), Path.Combine(root, "Archive", "Recovery"),
            Path.Combine(root, "trusted-keys.json"), Path.Combine(root, "management.json"),
            Path.Combine(root, "management-state.json"), Path.Combine(root, "active-policy.json"),
            Path.Combine(AppContext.BaseDirectory, "integrity-manifest.json"), Path.Combine(root, "policy-state.bin"),
            Path.Combine(root, "device-identity.bin"), Path.Combine(root, "portal-credential.bin"),
            Path.Combine(root, "portal-checkin-status.json"),
            Path.Combine(root, "TelemetryQueue"), Path.Combine(root, "CommandResults"),
            Path.Combine(root, "heartbeat-latest.json"), Path.Combine(root, "quarantine-state.bin"),
            Path.Combine(root, "quarantine-status.json"), Path.Combine(root, "tamper-event-latest.json"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(InboxDirectory);
        Directory.CreateDirectory(AcceptedDirectory);
        Directory.CreateDirectory(RejectedDirectory);
        Directory.CreateDirectory(RecoveryArchiveDirectory);
        Directory.CreateDirectory(TelemetryQueueDirectory);
        Directory.CreateDirectory(CommandResultsDirectory);
    }
}

internal sealed class JsonPolicyKeyProvider : IPolicyPublicKeyProvider
{
    private readonly IReadOnlyDictionary<string, string> _keys;
    private JsonPolicyKeyProvider(IReadOnlyDictionary<string, string> keys) => _keys = keys;

    public static JsonPolicyKeyProvider Load(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path)) return new JsonPolicyKeyProvider(new Dictionary<string, string>());
        var document = JsonSerializer.Deserialize<TrustedKeyDocument>(File.ReadAllBytes(path), options)
                       ?? new TrustedKeyDocument(Array.Empty<TrustedKeyEntry>());
        return new JsonPolicyKeyProvider(document.Keys.ToDictionary(x => x.KeyId, x => x.PublicKeyPem, StringComparer.Ordinal));
    }

    public ECDsa? GetKey(string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var pem)) return null;
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }
}

internal sealed record TrustedKeyDocument(IReadOnlyList<TrustedKeyEntry> Keys);
internal sealed record TrustedKeyEntry(string KeyId, string PublicKeyPem);
