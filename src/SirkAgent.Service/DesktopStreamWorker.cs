using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
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
            SingleReader = true,
            SingleWriter = true
        });
    private int _viewers;
    private long _inputCommands;
    private long _capturedFrames;
    private long _uploadedFrames;
    private long _droppedFrames;
    private long _binaryCaptures;
    private long _legacyCaptures;
    private int _maxWidth = 1920;
    private int _quality = 45;
    private int _forceFullFrame = 1;
    private int _monitorIndex;
    private int _sessionId = -1;
    private int _targetKbps = 1000;
    private int _targetFps = 60;
    private int _profileTargetFps = 60;
    private int _dirtyRegionMode;
    private int _deltaScalePercent = 100;
    private int _profileDeltaScalePercent = 100;
    private int _h264Available = 1;
    private int _profileQuality = 85;
    private string _imageEncoding = "webp";
    private long _lastStreamStatusWrite;
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
                var frameStarted = Stopwatch.GetTimestamp();
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
                    type = Volatile.Read(ref _dirtyRegionMode) != 0 ||
                           Volatile.Read(ref _h264Available) == 0 ? "snapshot" : "video-frame",
                    maxWidth = Volatile.Read(ref _maxWidth),
                    quality = Volatile.Read(ref _quality),
                    targetKbps = Volatile.Read(ref _targetKbps),
                    targetFps = Volatile.Read(ref _targetFps),
                    deltaScalePercent = Volatile.Read(ref _deltaScalePercent),
                    imageEncoding = Volatile.Read(ref _imageEncoding),
                    forceFull
                }, Json);
                string responseJson;
                byte[] frameBytes;
                var sessionTimer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var binary = await InteractiveSessionClient.SendBinaryCaptureAsync(sessionId, request, token);
                    responseJson = binary.HeaderJson;
                    frameBytes = binary.Payload;
                    Interlocked.Increment(ref _binaryCaptures);
                }
                catch (IOException)
                {
                    var responseLine = await InteractiveSessionClient.SendCaptureAsync(sessionId, request, token);
                    if (string.IsNullOrWhiteSpace(responseLine))
                        throw new IOException("Session broker returned no frame.");
                    responseJson = responseLine;
                    using var fallback = JsonDocument.Parse(responseJson);
                    frameBytes = fallback.RootElement.TryGetProperty("imageBase64", out var image) &&
                                 image.ValueKind == JsonValueKind.String
                        ? Convert.FromBase64String(image.GetString() ?? "") : [];
                    Interlocked.Increment(ref _legacyCaptures);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    var responseLine = await InteractiveSessionClient.SendCaptureAsync(sessionId, request, token);
                    if (string.IsNullOrWhiteSpace(responseLine))
                        throw new IOException("Session broker returned no frame.");
                    responseJson = responseLine;
                    using var fallback = JsonDocument.Parse(responseJson);
                    frameBytes = fallback.RootElement.TryGetProperty("imageBase64", out var image) &&
                                 image.ValueKind == JsonValueKind.String
                        ? Convert.FromBase64String(image.GetString() ?? "") : [];
                    Interlocked.Increment(ref _legacyCaptures);
                }
                using var responseDocument = JsonDocument.Parse(responseJson);
                sessionTimer.Stop();
                var root = responseDocument.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                {
                    if (Volatile.Read(ref _h264Available) != 0)
                        Volatile.Write(ref _h264Available, 0);
                    throw new InvalidDataException(root.TryGetProperty("error", out var captureError)
                        ? captureError.GetString() ?? "Session broker rejected desktop capture."
                        : "Session broker rejected desktop capture.");
                }
                if (root.TryGetProperty("code", out var code) &&
                    string.Equals(code.GetString(), "DESKTOP_NO_CHANGE", StringComparison.Ordinal))
                {
                    continue;
                }
                var data = root.GetProperty("data");
                var encoding = data.TryGetProperty("encoding", out var encodingValue)
                    ? encodingValue.GetString() ?? "JPEG" : "JPEG";
                var sourceWidth = Number(root, "width");
                var sourceHeight = Number(root, "height");
                var encodedWidth = Number(data, "encodedWidth");
                var encodedHeight = Number(data, "encodedHeight");
                var frame = new DesktopFrame(
                    frameBytes,
                    encodedWidth == "0" ? sourceWidth : encodedWidth,
                    encodedHeight == "0" ? sourceHeight : encodedHeight,
                    sourceWidth, sourceHeight,
                    Number(data, "captureMilliseconds"), Number(data, "encodeMilliseconds"),
                    sessionTimer.Elapsed.TotalMilliseconds.ToString(CultureInfo.InvariantCulture),
                    data.TryGetProperty("captureBackend", out var backend) ? backend.GetString() ?? "" : "",
                    Bool(data, "fullFrame"),
                    data.TryGetProperty("patches", out var patches) ? patches.GetRawText() : "[]",
                    data.TryGetProperty("moves", out var moves) ? moves.GetRawText() : "[]",
                    Number(data, "dirtyRectangleCount"), Number(data, "dirtyPixelRatio"),
                    Number(data, "deltaScalePercent"), Bool(data, "refinement"),
                    Number(data, "accumulatedFrames"),
                    Number(data, "cursorX"), Number(data, "cursorY"),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ContentTypeForEncoding(encoding),
                    encoding, Bool(data, "keyFrame"), Bool(data, "cursorOnly"));
                var enqueued = false;
                if (Volatile.Read(ref _dirtyRegionMode) != 0)
                {
                    await _frames.Writer.WriteAsync(frame, token);
                    enqueued = true;
                }
                else if (_frames.Writer.TryWrite(frame)) enqueued = true;
                else
                {
                    Interlocked.Increment(ref _droppedFrames);
                    Interlocked.Exchange(ref _forceFullFrame, 1);
                }
                if (enqueued) Interlocked.Increment(ref _capturedFrames);
                var frameInterval = TimeSpan.FromSeconds(1d / Volatile.Read(ref _targetFps));
                var remaining = frameInterval - Stopwatch.GetElapsedTime(frameStarted);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
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
                var viewerActive = control.RootElement.TryGetProperty("viewerActive", out var active) && active.GetBoolean();
                Volatile.Write(ref _viewers, viewerActive ? 1 : 0);
                AtomicFile.WriteJson(Path.Combine(paths.Root, "desktop-control-status.json"), new
                {
                    ok = true, timestampUtc = DateTimeOffset.UtcNow, viewerActive,
                    endpoint = ControlEndpoint(credential.Endpoint).GetLeftPart(UriPartial.Authority),
                    queuedInputs = control.RootElement.TryGetProperty("inputs", out var queuedInputs) &&
                                   queuedInputs.ValueKind == JsonValueKind.Array ? queuedInputs.GetArrayLength() : 0
                }, Json);
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
            var requestedWidth = Math.Clamp(Integer(input, "maxWidth"), 640, 1920);
            var requestedSession = Integer(input, "sessionId");
            var requestedSessionId = requestedSession is >= 0 and <= 65535 ? requestedSession : -1;
            var previousWidth = Volatile.Read(ref _maxWidth);
            var requestedFpsValue = Integer(input, "targetFps");
            var requestedFps = requestedFpsValue == 0 ? 60 : Math.Clamp(requestedFpsValue, 5, 120);
            var previousFps = Volatile.Read(ref _targetFps);
            var requestedDirtyRegionMode = string.Equals(Text(input, "frameMode"), "tiles",
                StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var requestedDeltaScaleValue = Integer(input, "deltaScalePercent");
            var requestedDeltaScale = requestedDeltaScaleValue == 0 ? 100 :
                Math.Clamp(requestedDeltaScaleValue, 10, 100);
            var requestedImageEncoding = NormalizeImageEncoding(Text(input, "imageEncoding"));
            var previousDirtyRegionMode = Volatile.Read(ref _dirtyRegionMode);
            var previousSessionId = Volatile.Read(ref _sessionId);
            var previousImageEncoding = Volatile.Read(ref _imageEncoding);
            if (requestedWidth != previousWidth || requestedFps != previousFps ||
                requestedDirtyRegionMode != previousDirtyRegionMode || requestedSessionId != previousSessionId ||
                !string.Equals(requestedImageEncoding, previousImageEncoding, StringComparison.Ordinal))
            {
                var recycleSessionId = InteractiveSessionPipe.Resolve(
                    previousSessionId >= 0 ? previousSessionId : requestedSessionId >= 0 ? requestedSessionId : null);
                InteractiveSessionClient.Invalidate(recycleSessionId);
                InteractiveSessionPipe.Terminate(recycleSessionId);
                InteractiveSessionPipe.EnsureAvailable(
                    requestedSessionId >= 0 ? requestedSessionId : recycleSessionId);
            }
            Volatile.Write(ref _maxWidth, requestedWidth);
            var requestedQualityValue = Integer(input, "quality");
            var requestedQuality = requestedQualityValue == 0 ? 85 :
                Math.Clamp(requestedQualityValue, 10, 100);
            Volatile.Write(ref _quality, requestedQuality);
            Volatile.Write(ref _profileQuality, requestedQuality);
            Volatile.Write(ref _imageEncoding, requestedImageEncoding);
            Volatile.Write(ref _targetKbps, Math.Clamp(Integer(input, "targetKbps"), 300, 8000));
            Volatile.Write(ref _targetFps, requestedFps);
            Volatile.Write(ref _profileTargetFps, requestedFps);
            Volatile.Write(ref _dirtyRegionMode, requestedDirtyRegionMode);
            Volatile.Write(ref _deltaScalePercent, requestedDeltaScale);
            Volatile.Write(ref _profileDeltaScalePercent, requestedDeltaScale);
            Volatile.Write(ref _monitorIndex, Math.Clamp(Integer(input, "monitorIndex"), 0, 15));
            Volatile.Write(ref _sessionId, requestedSessionId);
            Interlocked.Exchange(ref _forceFullFrame, 1);
            Volatile.Write(ref _h264Available, 1);
            Interlocked.Increment(ref _inputCommands);
            return;
        }
        if (string.Equals(Text(input, "action"), "requestKeyframe", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref _forceFullFrame, 1);
            Interlocked.Increment(ref _inputCommands);
            return;
        }
        if (string.Equals(Text(input, "action"), "streamStop", StringComparison.Ordinal))
        {
            Volatile.Write(ref _viewers, 0);
            Interlocked.Exchange(ref _forceFullFrame, 1);
            var selectedSessionId = Volatile.Read(ref _sessionId);
            var streamSessionId = InteractiveSessionPipe.Resolve(selectedSessionId >= 0 ? selectedSessionId : null);
            if (InteractiveSessionPipe.IsAvailable(streamSessionId))
            {
                try
                {
                    await InteractiveSessionClient.SendInputAsync(streamSessionId,
                        JsonSerializer.Serialize(new { type = "stream-stop" }, Json), token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    logger.LogDebug(error, "Interactive session stream cleanup failed.");
                    InteractiveSessionClient.Invalidate(streamSessionId);
                }
            }
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
        var paths = ManagementPaths.CreateDefault();
        ClientWebSocket? socket = null;
        Task? receive = null;
        long sequence = 0;
        await foreach (var frame in _frames.Reader.ReadAllAsync(token))
        {
            try
            {
                var credential = new PortalCredentialStore(paths.PortalCredentialPath,
                    new DpapiMachineStateProtector()).Load();
                if (credential is null) continue;
                if (socket is null || socket.State != WebSocketState.Open)
                {
                    socket?.Dispose();
                    socket = await ConnectDesktopSocketAsync(credential, token);
                    var connectedSocket = socket;
                    receive = ReceiveControlsAsync(connectedSocket, token);
                    _ = receive.ContinueWith(_ =>
                    {
                        try { connectedSocket.Abort(); } catch { }
                    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
                var metadata = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    width = int.Parse(frame.Width, CultureInfo.InvariantCulture),
                    height = int.Parse(frame.Height, CultureInfo.InvariantCulture),
                    sourceWidth = int.Parse(frame.SourceWidth, CultureInfo.InvariantCulture),
                    sourceHeight = int.Parse(frame.SourceHeight, CultureInfo.InvariantCulture),
                    captureMilliseconds = double.Parse(frame.CaptureMilliseconds, CultureInfo.InvariantCulture),
                    encodeMilliseconds = double.Parse(frame.EncodeMilliseconds, CultureInfo.InvariantCulture),
                    sessionMilliseconds = double.Parse(frame.SessionMilliseconds, CultureInfo.InvariantCulture),
                    captureBackend = frame.CaptureBackend, fullFrame = frame.FullFrame,
                    patches = JsonSerializer.Deserialize<JsonElement>(frame.Patches),
                    moves = JsonSerializer.Deserialize<JsonElement>(frame.Moves),
                    dirtyRectangleCount = int.Parse(frame.DirtyRectangleCount, CultureInfo.InvariantCulture),
                    dirtyPixelRatio = double.Parse(frame.DirtyPixelRatio, CultureInfo.InvariantCulture),
                    deltaScalePercent = int.Parse(frame.DeltaScalePercent, CultureInfo.InvariantCulture),
                    refinement = frame.Refinement,
                    accumulatedFrames = int.Parse(frame.AccumulatedFrames, CultureInfo.InvariantCulture),
                    cursorX = int.Parse(frame.CursorX, CultureInfo.InvariantCulture),
                    cursorY = int.Parse(frame.CursorY, CultureInfo.InvariantCulture),
                    capturedAtUnixMilliseconds = frame.CapturedAtUnixMilliseconds,
                    encodedBytes = frame.Bytes.Length, contentType = frame.ContentType,
                    encoding = frame.Encoding, keyFrame = frame.KeyFrame,
                    cursorOnly = frame.CursorOnly,
                    targetFps = Volatile.Read(ref _targetFps),
                    targetKbps = Volatile.Read(ref _targetKbps)
                }, Json);
                var packet = new byte[4 + metadata.Length + frame.Bytes.Length];
                BinaryPrimitives.WriteInt32BigEndian(packet, metadata.Length);
                Buffer.BlockCopy(metadata, 0, packet, 4, metadata.Length);
                Buffer.BlockCopy(frame.Bytes, 0, packet, 4 + metadata.Length, frame.Bytes.Length);
                var sendTimer = System.Diagnostics.Stopwatch.StartNew();
                await socket.SendAsync(packet, WebSocketMessageType.Binary, true, token);
                sendTimer.Stop();
                sequence++;
                Interlocked.Increment(ref _uploadedFrames);
                var bitrateKbps = AdaptToBandwidth(frame.Bytes.Length);
                var statusTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                if (statusTimestamp - _lastStreamStatusWrite >= System.Diagnostics.Stopwatch.Frequency / 4)
                {
                    _lastStreamStatusWrite = statusTimestamp;
                    AtomicFile.WriteJson(Path.Combine(paths.Root, "desktop-stream-status.json"), new
                    {
                        ok = true, timestampUtc = DateTimeOffset.UtcNow, frameBytes = frame.Bytes.Length,
                        viewers = Volatile.Read(ref _viewers), sequence,
                        queueCapacity = 1, latestFrameWins = true,
                        capturedFrames = Interlocked.Read(ref _capturedFrames),
                        uploadedFrames = Interlocked.Read(ref _uploadedFrames),
                        droppedFrames = Interlocked.Read(ref _droppedFrames),
                        sendMilliseconds = Math.Round(sendTimer.Elapsed.TotalMilliseconds, 2),
                        inputCommands = Interlocked.Read(ref _inputCommands),
                        sessionTransport = Interlocked.Read(ref _binaryCaptures) > 0 ? "BINARY_PIPE" : "JSON_BASE64",
                        binaryCaptures = Interlocked.Read(ref _binaryCaptures),
                        legacyCaptures = Interlocked.Read(ref _legacyCaptures),
                        maxWidth = Volatile.Read(ref _maxWidth),
                        quality = Volatile.Read(ref _quality),
                        targetKbps = Volatile.Read(ref _targetKbps),
                        targetFps = Volatile.Read(ref _targetFps),
                        frameMode = Volatile.Read(ref _dirtyRegionMode) != 0 ? "tiles" : "h264",
                        imageEncoding = Volatile.Read(ref _imageEncoding),
                        deltaScalePercent = Volatile.Read(ref _deltaScalePercent),
                        bitrateKbps
                    }, Json);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                socket?.Dispose();
                socket = null;
                WriteStatus(false, "UPLOAD: " + error);
                logger.LogDebug(error, "Direct desktop upload failed.");
            }
        }
        socket?.Dispose();
        if (receive is not null) try { await receive; } catch { }
    }

    private async Task ReceiveControlsAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var payload = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Unexpected desktop control message type.");
                payload.Write(buffer, 0, result.Count);
                if (payload.Length > 256 * 1024) throw new InvalidDataException("Desktop control message too large.");
            } while (!result.EndOfMessage);
            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "input" &&
                root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                await ExecuteInputAsync(input, token);
        }
    }

    private static async Task<ClientWebSocket> ConnectDesktopSocketAsync(
        PortalCredential credential, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credential.DeviceToken);
        socket.Options.SetRequestHeader("X-SIRK-Tenant", credential.TenantId);
        socket.Options.SetRequestHeader("X-SIRK-Device", credential.DeviceId);
        using var request = new HttpRequestMessage(HttpMethod.Get, DesktopSocketEndpoint(credential.Endpoint));
        Sign(request, [], credential);
        foreach (var name in new[] { "X-SIRK-Timestamp", "X-SIRK-Nonce", "X-SIRK-Signature" })
            socket.Options.SetRequestHeader(name, request.Headers.GetValues(name).Single());
        await socket.ConnectAsync(DesktopSocketEndpoint(credential.Endpoint), token);
        return socket;
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
        var targetFps = Volatile.Read(ref _targetFps);
        var deltaScale = Volatile.Read(ref _deltaScalePercent);
        if (bitrateKbps > target * 1.1)
        {
            if (Volatile.Read(ref _dirtyRegionMode) != 0 && deltaScale > 10)
                Volatile.Write(ref _deltaScalePercent, Math.Max(10, deltaScale - 5));
            else if (!string.Equals(Volatile.Read(ref _imageEncoding), "png", StringComparison.Ordinal) &&
                     quality > 20) Volatile.Write(ref _quality, Math.Max(10, quality - 3));
            else if (Volatile.Read(ref _dirtyRegionMode) != 0 && targetFps > 5)
                Volatile.Write(ref _targetFps, Math.Max(5, targetFps - 5));
            _lastAdaptiveChange = now;
        }
        else if (bitrateKbps < target * 0.55)
        {
            var requestedQuality = Volatile.Read(ref _profileQuality);
            var requestedFps = Volatile.Read(ref _profileTargetFps);
            var requestedDeltaScale = Volatile.Read(ref _profileDeltaScalePercent);
            if (Volatile.Read(ref _dirtyRegionMode) != 0 && targetFps < requestedFps)
                Volatile.Write(ref _targetFps, Math.Min(requestedFps, targetFps + 5));
            else if (Volatile.Read(ref _dirtyRegionMode) != 0 && deltaScale < requestedDeltaScale)
                Volatile.Write(ref _deltaScalePercent, Math.Min(requestedDeltaScale, deltaScale + 5));
            else if (quality < requestedQuality) Volatile.Write(ref _quality, Math.Min(requestedQuality, quality + 1));
            _lastAdaptiveChange = now;
        }
        return bitrateKbps;
    }

    private static string NormalizeImageEncoding(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "png" => "png",
            "jpeg" or "jpg" => "jpeg",
            "webp" => "webp",
            _ => "webp"
        };

    private static string ContentTypeForEncoding(string encoding) =>
        encoding.ToUpperInvariant() switch
        {
            "PNG" => "image/png",
            "WEBP" => "image/webp",
            var value when value.StartsWith("H264", StringComparison.Ordinal) => "video/h264",
            _ => "image/jpeg"
        };

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
        return new UriBuilder(source) { Path = "/api/v1/agent/desktop/frame", Query = "" }.Uri;
    }

    private static Uri DesktopSocketEndpoint(string endpoint)
    {
        var source = new Uri(endpoint);
        return new UriBuilder(source)
        {
            Scheme = source.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/api/v1/agent/desktop/stream", Query = ""
        }.Uri;
    }

    private static Uri ControlEndpoint(string endpoint)
    {
        var source = new Uri(endpoint);
        return new UriBuilder(source) { Path = "/api/v1/agent/desktop/control", Query = "" }.Uri;
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
    string SourceWidth, string SourceHeight,
    string CaptureMilliseconds, string EncodeMilliseconds, string SessionMilliseconds, string CaptureBackend,
    bool FullFrame, string Patches, string Moves, string DirtyRectangleCount, string DirtyPixelRatio,
    string DeltaScalePercent, bool Refinement, string AccumulatedFrames, string CursorX, string CursorY,
    long CapturedAtUnixMilliseconds, string ContentType, string Encoding, bool KeyFrame, bool CursorOnly);
