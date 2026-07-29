using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirkAgent.Policy;
using SirkAgent.Service.Core;

namespace SirkAgent.Service;

internal sealed class DesktopStreamWorker(ILogger<DesktopStreamWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<DesktopFrame> _frames = Channel.CreateBounded<DesktopFrame>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });
    private int _viewers;
    private long _inputCommands;
    private int _maxWidth = 1920;
    private int _quality = 45;
    private int _forceFullFrame = 1;
    private int _monitorIndex;
    private int _sessionId = -1;
    private int _targetKbps = 1000;
    private int _profileMaxWidth = 1920;
    private int _profileQuality = 72;
    private readonly Queue<(DateTimeOffset At, int Bytes)> _bandwidthWindow = new();
    private DateTimeOffset _lastAdaptiveChange = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var upload = UploadLoopAsync(stoppingToken);
        var control = ControlLoopAsync(stoppingToken);
        try { await CaptureLoopAsync(stoppingToken); }
        finally
        {
            _frames.Writer.TryComplete();
            await Task.WhenAll(upload, control);
        }
    }

    private async Task CaptureLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (Volatile.Read(ref _viewers) == 0)
            {
                await Task.Delay(50, token);
                continue;
            }
            try
            {
                var selectedSessionId = Volatile.Read(ref _sessionId);
                var sessionId = InteractiveSessionPipe.Resolve(selectedSessionId >= 0 ? selectedSessionId : null);
                if (!InteractiveSessionPipe.IsAvailable(sessionId))
                {
                    InteractiveSessionPipe.EnsureAvailable(sessionId);
                    InteractiveSessionClient.Invalidate(sessionId);
                }
                var forceFull = Interlocked.Exchange(ref _forceFullFrame, 0) != 0;
                var request = JsonSerializer.Serialize(new
                {
                    type = "snapshot", monitorIndex = Volatile.Read(ref _monitorIndex),
                    maxWidth = Volatile.Read(ref _maxWidth),
                    quality = Volatile.Read(ref _quality),
                    forceFull
                }, Json);
                var responseLine = await InteractiveSessionClient.SendCaptureAsync(sessionId, request, token);
                if (string.IsNullOrWhiteSpace(responseLine)) throw new IOException("Session broker returned no frame.");
                using var responseDocument = JsonDocument.Parse(responseLine);
                var root = responseDocument.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                    throw new InvalidDataException(root.TryGetProperty("error", out var captureError)
                        ? captureError.GetString() ?? "Session broker rejected desktop capture."
                        : "Session broker rejected desktop capture.");
                if (root.TryGetProperty("code", out var code) &&
                    string.Equals(code.GetString(), "DESKTOP_NO_CHANGE", StringComparison.Ordinal))
                {
                    continue;
                }
                var data = root.GetProperty("data");
                var frame = new DesktopFrame(
                    Convert.FromBase64String(root.GetProperty("imageBase64").GetString() ?? ""),
                    Number(root, "width"), Number(root, "height"),
                    Number(data, "captureMilliseconds"), Number(data, "encodeMilliseconds"),
                    data.TryGetProperty("captureBackend", out var backend) ? backend.GetString() ?? "" : "",
                    Bool(data, "fullFrame"),
                    data.TryGetProperty("patches", out var patches) ? patches.GetRawText() : "[]",
                    data.TryGetProperty("moves", out var moves) ? moves.GetRawText() : "[]",
                    Number(data, "cursorX"), Number(data, "cursorY"),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (!_frames.Writer.TryWrite(frame))
                {
                    if (_frames.Reader.TryRead(out _))
                    {
                        Interlocked.Exchange(ref _forceFullFrame, 1);
                        continue;
                    }
                    await _frames.Writer.WriteAsync(frame, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                WriteStatus(false, error.Message);
                logger.LogDebug(error, "Direct desktop capture cycle failed.");
                await Task.Delay(500, token);
            }
        }
    }

    private async Task ControlLoopAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        var paths = ManagementPaths.CreateDefault();
        while (!token.IsCancellationRequested)
        {
            try
            {
                var credential = new PortalCredentialStore(paths.PortalCredentialPath,
                    new DpapiMachineStateProtector()).Load();
                if (credential is null)
                {
                    await Task.Delay(1000, token);
                    continue;
                }
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    credential.TenantId, credential.DeviceId, waitMilliseconds = 25000
                }, Json);
                using var request = new HttpRequestMessage(HttpMethod.Post, ControlEndpoint(credential.Endpoint))
                {
                    Content = new ByteArrayContent(body)
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.DeviceToken);
                request.Headers.Add("X-SIRK-Tenant", credential.TenantId);
                request.Headers.Add("X-SIRK-Device", credential.DeviceId);
                Sign(request, body, credential);
                using var response = await client.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                using var control = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                Volatile.Write(ref _viewers,
                    control.RootElement.TryGetProperty("viewerActive", out var active) && active.GetBoolean() ? 1 : 0);
                if (control.RootElement.TryGetProperty("inputs", out var inputs) &&
                    inputs.ValueKind == JsonValueKind.Array)
                    foreach (var input in inputs.EnumerateArray())
                        await ExecuteInputAsync(input, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                Volatile.Write(ref _viewers, 0);
                WriteStatus(false, error.Message);
                logger.LogDebug(error, "Desktop control channel failed.");
                await Task.Delay(1000, token);
            }
        }
    }

    private async Task ExecuteInputAsync(JsonElement input, CancellationToken token)
    {
        if (string.Equals(Text(input, "action"), "streamProfile", StringComparison.Ordinal))
        {
            Volatile.Write(ref _maxWidth, Math.Clamp(Integer(input, "maxWidth"), 640, 1920));
            Volatile.Write(ref _quality, Math.Clamp(Integer(input, "quality"), 25, 80));
            Volatile.Write(ref _profileMaxWidth, Volatile.Read(ref _maxWidth));
            Volatile.Write(ref _profileQuality, Volatile.Read(ref _quality));
            Volatile.Write(ref _targetKbps, Math.Clamp(Integer(input, "targetKbps"), 300, 8000));
            Volatile.Write(ref _monitorIndex, Math.Clamp(Integer(input, "monitorIndex"), 0, 15));
            var requestedSession = Integer(input, "sessionId");
            Volatile.Write(ref _sessionId, requestedSession is >= 0 and <= 65535 ? requestedSession : -1);
            Interlocked.Exchange(ref _forceFullFrame, 1);
            Interlocked.Increment(ref _inputCommands);
            return;
        }
        if (string.Equals(Text(input, "action"), "requestKeyframe", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _forceFullFrame, 1);
            Interlocked.Increment(ref _inputCommands);
            return;
        }
        var sessionId = input.TryGetProperty("sessionId", out var selectedSession)
            ? selectedSession.GetInt32()
            : InteractiveSessionPipe.Resolve(null);
        var request = JsonSerializer.Serialize(new
        {
            type = "input",
            action = Text(input, "action"),
            x = Integer(input, "x"),
            y = Integer(input, "y"),
            delta = Integer(input, "delta"),
            text = Text(input, "text"),
            key = Text(input, "key"),
            modifiers = Text(input, "modifiers"),
            fileName = Text(input, "fileName"),
            fileBase64 = Text(input, "fileBase64")
        }, Json);
        await InteractiveSessionClient.SendInputAsync(sessionId, request, token);
        Interlocked.Increment(ref _inputCommands);
    }

    private async Task UploadLoopAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var paths = ManagementPaths.CreateDefault();
        await foreach (var frame in _frames.Reader.ReadAllAsync(token))
        {
            try
            {
                var credential = new PortalCredentialStore(paths.PortalCredentialPath,
                    new DpapiMachineStateProtector()).Load();
                if (credential is null) continue;
                using var upload = new HttpRequestMessage(HttpMethod.Post, FrameEndpoint(credential.Endpoint))
                {
                    Content = new ByteArrayContent(frame.Bytes)
                };
                upload.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.DeviceToken);
                upload.Headers.Add("X-SIRK-Tenant", credential.TenantId);
                upload.Headers.Add("X-SIRK-Device", credential.DeviceId);
                upload.Headers.Add("X-SIRK-Width", frame.Width);
                upload.Headers.Add("X-SIRK-Height", frame.Height);
                upload.Headers.Add("X-SIRK-Capture-Ms", frame.CaptureMilliseconds);
                upload.Headers.Add("X-SIRK-Encode-Ms", frame.EncodeMilliseconds);
                upload.Headers.Add("X-SIRK-Capture-Backend", frame.CaptureBackend);
                upload.Headers.Add("X-SIRK-Full-Frame", frame.FullFrame ? "1" : "0");
                upload.Headers.Add("X-SIRK-Patches",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(frame.Patches)));
                upload.Headers.Add("X-SIRK-Moves",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(frame.Moves)));
                upload.Headers.Add("X-SIRK-Cursor-X", frame.CursorX);
                upload.Headers.Add("X-SIRK-Cursor-Y", frame.CursorY);
                upload.Headers.Add("X-SIRK-Captured-At", frame.CapturedAtUnixMilliseconds.ToString());
                Sign(upload, frame.Bytes, credential);
                using var uploaded = await client.SendAsync(upload, token);
                uploaded.EnsureSuccessStatusCode();
                using var published = JsonDocument.Parse(await uploaded.Content.ReadAsByteArrayAsync(token));
                var viewers = published.RootElement.TryGetProperty("viewers", out var count) ? count.GetInt32() : 0;
                Volatile.Write(ref _viewers, viewers);
                var bitrateKbps = AdaptToBandwidth(frame.Bytes.Length);
                AtomicFile.WriteJson(Path.Combine(paths.Root, "desktop-stream-status.json"), new
                {
                    ok = true, timestampUtc = DateTimeOffset.UtcNow, frameBytes = frame.Bytes.Length, viewers,
                    sequence = published.RootElement.TryGetProperty("sequence", out var sequence)
                        ? sequence.GetInt64() : 0,
                    queueCapacity = 1, latestFrameWins = true,
                    inputCommands = Interlocked.Read(ref _inputCommands),
                    maxWidth = Volatile.Read(ref _maxWidth),
                    quality = Volatile.Read(ref _quality),
                    targetKbps = Volatile.Read(ref _targetKbps),
                    bitrateKbps
                }, Json);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                Volatile.Write(ref _viewers, 0);
                WriteStatus(false, error.Message);
                logger.LogDebug(error, "Direct desktop upload failed.");
            }
        }
    }

    private int AdaptToBandwidth(int bytes)
    {
        var now = DateTimeOffset.UtcNow;
        _bandwidthWindow.Enqueue((now, bytes));
        while (_bandwidthWindow.Count > 0 && now - _bandwidthWindow.Peek().At > TimeSpan.FromSeconds(2))
            _bandwidthWindow.Dequeue();
        if (_bandwidthWindow.Count < 2) return 0;
        var elapsed = Math.Max(0.1, (now - _bandwidthWindow.Peek().At).TotalSeconds);
        var bitrateKbps = (int)Math.Round(_bandwidthWindow.Sum(value => (long)value.Bytes) * 8d /
                                          elapsed / 1000d);
        if (elapsed < 1 || now - _lastAdaptiveChange < TimeSpan.FromMilliseconds(500))
            return bitrateKbps;
        var target = Volatile.Read(ref _targetKbps);
        var quality = Volatile.Read(ref _quality);
        var width = Volatile.Read(ref _maxWidth);
        if (bitrateKbps > target * 1.1)
        {
            if (quality > 30) Volatile.Write(ref _quality, Math.Max(25, quality - 3));
            else if (width > 640) Volatile.Write(ref _maxWidth, Math.Max(640, width - 160));
            _lastAdaptiveChange = now;
        }
        else if (bitrateKbps < target * 0.55)
        {
            var requestedWidth = Volatile.Read(ref _profileMaxWidth);
            var requestedQuality = Volatile.Read(ref _profileQuality);
            if (width < requestedWidth) Volatile.Write(ref _maxWidth, Math.Min(requestedWidth, width + 160));
            else if (quality < requestedQuality) Volatile.Write(ref _quality, Math.Min(requestedQuality, quality + 1));
            _lastAdaptiveChange = now;
        }
        return bitrateKbps;
    }

    private static string Number(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) ? value.ToString() : "0";
    private static bool Bool(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    private static int Integer(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static Uri FrameEndpoint(string endpoint)
    {
        var source = new Uri(endpoint);
        return new UriBuilder(source) { Path = "/api/agent/v1/desktop/frame", Query = "" }.Uri;
    }

    private static Uri ControlEndpoint(string endpoint)
    {
        var source = new Uri(endpoint);
        return new UriBuilder(source) { Path = "/api/agent/v1/desktop/control", Query = "" }.Uri;
    }

    private static void WriteStatus(bool ok, string error)
    {
        var root = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
        AtomicFile.WriteJson(Path.Combine(root, "desktop-stream-status.json"),
            new { ok, timestampUtc = DateTimeOffset.UtcNow, error }, Json);
    }

    private static void Sign(HttpRequestMessage request, byte[] payload, PortalCredential credential)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var prefix = Encoding.UTF8.GetBytes(timestamp + "\n" + nonce + "\n");
        var signed = new byte[prefix.Length + payload.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(payload, 0, signed, prefix.Length, payload.Length);
        byte[] signature;
        if (!string.IsNullOrWhiteSpace(credential.KeyName))
            signature = DeviceSigningKey.Sign(credential.KeyName, signed);
        else
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(credential.PrivateKeyPkcs8!), out _);
            signature = key.SignData(signed, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        request.Headers.Add("X-SIRK-Timestamp", timestamp);
        request.Headers.Add("X-SIRK-Nonce", nonce);
        request.Headers.Add("X-SIRK-Signature", Convert.ToBase64String(signature));
    }
}

internal sealed record DesktopFrame(byte[] Bytes, string Width, string Height,
    string CaptureMilliseconds, string EncodeMilliseconds, string CaptureBackend,
    bool FullFrame, string Patches, string Moves, string CursorX, string CursorY,
    long CapturedAtUnixMilliseconds);
