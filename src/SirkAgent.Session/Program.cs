using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace SirkAgent.Session;

internal static class Program
{
    private static readonly int SessionId = Process.GetCurrentProcess().SessionId;
    private static readonly string PipeName = "SIRK-Agent-Interactive-Session-" + SessionId;
    private static readonly string BinaryCapturePipeName = PipeName + "-Video";
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<ImageCodecInfo> JpegEncoder = new(() =>
        ImageCodecInfo.GetImageEncoders()
            .First(value => value.FormatID == ImageFormat.Jpeg.Guid));
    private static DateTimeOffset _lastActivitySampleUtc = DateTimeOffset.UtcNow;
    private static System.Drawing.Point? _lastCursorPosition;
    private static readonly object CaptureSync = new();
    private static readonly Dictionary<int, DxgiDesktopCapture> DxgiCaptures = [];
    private static readonly Dictionary<int, DateTimeOffset> LastFullFrames = [];
    private static readonly Dictionary<int, List<Rectangle>> PendingRefinementRegions = [];
    private static readonly Dictionary<int, long> LastDirtyFrameTimestamps = [];
    private static readonly Dictionary<int, DxgiH264Capture> H264Captures = [];
    private static SessionH264Encoder? _h264Encoder;
    private static long _lastVideoRequestTimestamp;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Any(value => string.Equals(value, "--codec-self-test",
                    StringComparison.OrdinalIgnoreCase)))
                return RunImageCodecSelfTest();
            await RunAsync();
            return 0;
        }
        catch (Exception error)
        {
            LogFatalStartup(error);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        using var singleInstance = new Mutex(true, "Local\\SIRK-Agent-Interactive-Session-" + SessionId,
            out var ownsMutex);
        if (!ownsMutex) return;
        _ = BinaryCaptureServerLoopAsync();
        _ = StreamResourceCleanupLoopAsync();
        while (true)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync();
                if (!Authorized(pipe))
                {
                    await pipe.DisposeAsync();
                    continue;
                }
                _ = HandlePipeAsync(pipe);
                pipe = null;
            }
            catch (Exception error)
            {
                if (pipe is not null) await pipe.DisposeAsync();
                LogError(error);
                await Task.Delay(1000);
            }
        }
    }

    private static async Task BinaryCaptureServerLoopAsync()
    {
        while (true)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(BinaryCapturePipeName);
                await pipe.WaitForConnectionAsync();
                if (!Authorized(pipe)) { await pipe.DisposeAsync(); continue; }
                _ = HandleBinaryCapturePipeAsync(pipe);
                pipe = null;
            }
            catch (Exception error)
            {
                if (pipe is not null) await pipe.DisposeAsync();
                LogError(error);
                await Task.Delay(250);
            }
        }
    }

    private static async Task HandleBinaryCapturePipeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            var size = new byte[4];
            while (pipe.IsConnected)
            {
                if (!await ReadExactlyOrEndAsync(pipe, size)) break;
                var requestLength = BinaryPrimitives.ReadInt32LittleEndian(size);
                if (requestLength is < 2 or > 64 * 1024) throw new InvalidDataException("Invalid video request.");
                var requestBytes = new byte[requestLength];
                await pipe.ReadExactlyAsync(requestBytes);
                var request = JsonSerializer.Deserialize<SessionRequest>(requestBytes, Json);
                SessionVideoPayload payload;
                try
                {
                    payload = request?.Type switch
                    {
                        "video-frame" => VideoFramePayload(request.MonitorIndex ?? -1,
                            request.MaxWidth ?? 1280, request.TargetKbps ?? 1000,
                            request.TargetFps ?? 60, request.ForceFull == true),
                        "snapshot" => SnapshotPayload(request.MonitorIndex ?? -1,
                            request.MaxWidth ?? 1280, request.Quality ?? 40,
                            request.TargetFps ?? 60, request.DeltaScalePercent ?? 100,
                            request.ImageEncoding ?? "webp", request.ForceFull == true),
                        _ => new SessionVideoPayload(new SessionResponse(false, "SESSION_REQUEST_INVALID",
                            null, null, null), [])
                    };
                }
                catch (Exception error)
                {
                    LogError(error);
                    payload = new SessionVideoPayload(new SessionResponse(false,
                        "SESSION_OPERATION_FAILED", null, null, null, Error: error.Message), []);
                }
                var header = JsonSerializer.SerializeToUtf8Bytes(payload.Response, Json);
                BinaryPrimitives.WriteInt32LittleEndian(size, header.Length);
                await pipe.WriteAsync(size);
                BinaryPrimitives.WriteInt32LittleEndian(size, payload.Bytes.Length);
                await pipe.WriteAsync(size);
                await pipe.WriteAsync(header);
                if (payload.Bytes.Length > 0) await pipe.WriteAsync(payload.Bytes);
                await pipe.FlushAsync();
            }
        }
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException();
            offset += read;
        }
        return true;
    }

    private static async Task HandlePipeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                    { AutoFlush = true };
                while (pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;
                    var request = JsonSerializer.Deserialize<SessionRequest>(line, Json);
                    SessionResponse response;
                    try
                    {
                        response = request?.Type switch
                        {
                            "monitors" => Monitors(),
                            "snapshot" => Snapshot(request.MonitorIndex ?? -1, request.MaxWidth ?? 1280,
                                request.Quality ?? 40, request.TargetFps ?? 60,
                                request.DeltaScalePercent ?? 100,
                                request.ImageEncoding ?? "webp", request.ForceFull == true),
                            "video-frame" => VideoFrame(request.MonitorIndex ?? -1, request.MaxWidth ?? 1280,
                                request.TargetKbps ?? 1000, request.TargetFps ?? 60, request.ForceFull == true),
                            "stream-stop" => MarkStreamStopped(),
                            "mouse" or "input" => Input(request),
                            "activity" => Activity(),
                            _ => new SessionResponse(false, "SESSION_REQUEST_INVALID", null, null, null)
                        };
                    }
                    catch (Exception error)
                    {
                        response = new SessionResponse(false,
                            request?.Type == "snapshot"
                                ? "DESKTOP_CAPTURE_UNAVAILABLE"
                                : "SESSION_OPERATION_FAILED",
                            null, null, null, Error: error.Message);
                        LogError(error);
                    }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json));
                }
            }
            catch (Exception error) { LogError(error); }
        }
    }

    private static void LogError(Exception error)
    {
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "SIRK", "Session");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "session-error.log"),
                DateTimeOffset.UtcNow.ToString("O") + " " + error + Environment.NewLine);
        }
        catch { }
    }

    private static void LogFatalStartup(Exception error)
    {
        LogError(error);
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData), "SIRK", "Agent");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "session-startup-error.log"),
                DateTimeOffset.UtcNow.ToString("O") + " sessionId=" + SessionId + " " +
                error + Environment.NewLine);
        }
        catch { }
    }

    private static NamedPipeServerStream CreatePipe(string? name = null)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        if (identity.User is not null)
            security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name ?? PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 1024 * 1024, 1024 * 1024, security);
    }

    private static bool Authorized(NamedPipeServerStream pipe)
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

    private static SessionResponse Monitors()
    {
        var monitors = NativeDesktop.Monitors().Select(monitor => new
        {
            index = monitor.Index,
            name = monitor.Name,
            primary = monitor.Primary,
            x = monitor.Bounds.X,
            y = monitor.Bounds.Y,
            width = monitor.Bounds.Width,
            height = monitor.Bounds.Height
        }).ToArray();
        return new SessionResponse(true, "DESKTOP_MONITORS_OK", null, null, null,
            JsonSerializer.SerializeToElement(new { sessionId = SessionId, monitors }, Json));
    }

    private static Rectangle CaptureBounds(int monitorIndex) =>
        NativeDesktop.CaptureBounds(monitorIndex);

    private static SessionResponse Snapshot(int monitorIndex, int maxWidth, int quality, int targetFps,
        int deltaScalePercent, string imageEncoding, bool requestedFull)
    {
        var payload = SnapshotPayload(monitorIndex, maxWidth, quality, targetFps, deltaScalePercent,
            imageEncoding, requestedFull);
        return payload.Bytes.Length == 0 ? payload.Response :
            payload.Response with { ImageBase64 = Convert.ToBase64String(payload.Bytes) };
    }

    private static SessionVideoPayload SnapshotPayload(int monitorIndex, int maxWidth, int quality,
        int targetFps, int deltaScalePercent, string imageEncoding, bool requestedFull)
    {
        lock (CaptureSync)
            return SnapshotPayloadLocked(monitorIndex, maxWidth, quality, targetFps, deltaScalePercent,
                imageEncoding, requestedFull);
    }

    private static SessionVideoPayload SnapshotPayloadLocked(int monitorIndex, int maxWidth, int quality,
        int targetFps, int deltaScalePercent, string imageEncoding, bool requestedFull)
    {
        Volatile.Write(ref _lastVideoRequestTimestamp, Stopwatch.GetTimestamp());
        var totalTimer = Stopwatch.StartNew();
        var bounds = CaptureBounds(monitorIndex);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Aktywna sesja nie udostępnia ekranu.");
        maxWidth = Math.Clamp(maxWidth, 640, 1920);
        quality = Math.Clamp(quality, 10, 100);
        imageEncoding = NormalizeImageEncoding(imageEncoding);
        targetFps = Math.Clamp(targetFps, 1, 120);
        deltaScalePercent = Math.Clamp(deltaScalePercent, 10, 100);
        var fullScale = Math.Min(1d, Math.Min((double)maxWidth / bounds.Width, 1080d / bounds.Height));
        var captureTimer = Stopwatch.StartNew();
        var captureTimeoutMilliseconds = Math.Clamp(1000 / targetFps, 1, 16);
        using var captured = CaptureDesktopBitmap(monitorIndex, bounds, captureTimeoutMilliseconds,
            borrowDxgiBitmap: true,
            out var captureBackend,
            out var dirtyRectangles, out var moveRectangles, out var accumulatedFrames);
        var bitmap = captured.Bitmap;
        captureTimer.Stop();
        var now = DateTimeOffset.UtcNow;
        var forceFull = requestedFull || !LastFullFrames.ContainsKey(monitorIndex);
        if (dirtyRectangles.Length == 0 && moveRectangles.Length > 0)
            dirtyRectangles = moveRectangles.Select(value =>
                new Rectangle(value.X, value.Y, value.Width, value.Height)).ToArray();
        var refinement = false;
        if (dirtyRectangles.Length > 0)
        {
            if (!PendingRefinementRegions.TryGetValue(monitorIndex, out var pending))
                PendingRefinementRegions[monitorIndex] = pending = [];
            pending.AddRange(dirtyRectangles);
            PendingRefinementRegions[monitorIndex] = MergeDirtyRectangles(pending, bounds);
            LastDirtyFrameTimestamps[monitorIndex] = Stopwatch.GetTimestamp();
        }
        else if (!forceFull && PendingRefinementRegions.TryGetValue(monitorIndex, out var pending) &&
                 pending.Count > 0 && LastDirtyFrameTimestamps.TryGetValue(monitorIndex, out var lastDirty) &&
                 Stopwatch.GetElapsedTime(lastDirty) >= TimeSpan.FromMilliseconds(75))
        {
            dirtyRectangles = pending.ToArray();
            PendingRefinementRegions.Remove(monitorIndex);
            LastDirtyFrameTimestamps.Remove(monitorIndex);
            refinement = true;
        }
        if (dirtyRectangles.Length == 0 && !forceFull)
        {
            totalTimer.Stop();
            return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_NO_CHANGE", null,
                bounds.Width, bounds.Height,
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = SessionId,
                    monitorIndex,
                    captureMilliseconds = Math.Round(captureTimer.Elapsed.TotalMilliseconds, 2),
                    agentFrameMilliseconds = Math.Round(totalTimer.Elapsed.TotalMilliseconds, 2),
                    captureBackend,
                    dirtyRectangleCount = 0,
                    dirtyPixelRatio = 0,
                    accumulatedFrames = 0,
                    encoding = "NONE"
                }, Json)), []);
        }
        var encodeTimer = Stopwatch.StartNew();
        using var output = new MemoryStream();
        var encodeFullFrame = forceFull || DirtyRegionsRequireFullFrame(dirtyRectangles, bounds);
        var encodeScale = encodeFullFrame || refinement
            ? fullScale
            : fullScale * deltaScalePercent / 100d;
        using var encodedBitmap = BuildEncodedBitmap(bitmap, bounds, dirtyRectangles, encodeScale,
            encodeFullFrame, out var fullFrame, out var patches);
        if (fullFrame)
        {
            LastFullFrames[monitorIndex] = now;
            PendingRefinementRegions.Remove(monitorIndex);
            LastDirtyFrameTimestamps.Remove(monitorIndex);
        }
        var encodedAs = EncodeBitmap(encodedBitmap, output, imageEncoding, quality);
        encodeTimer.Stop();
        var bytes = output.ToArray();
        totalTimer.Stop();
        var cursor = NativeDesktop.GetCursorPosition();
        return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_SNAPSHOT_OK", null,
            bounds.Width, bounds.Height, JsonSerializer.SerializeToElement(new
            {
                sessionId = SessionId,
                monitorIndex,
                encodedWidth = encodedBitmap.Width,
                encodedHeight = encodedBitmap.Height,
                fullFrame,
                patches,
                moves = fullFrame ? [] : moveRectangles,
                originX = bounds.Left,
                originY = bounds.Top,
                cursorX = Math.Clamp(cursor.X - bounds.Left, 0, Math.Max(0, bounds.Width - 1)),
                cursorY = Math.Clamp(cursor.Y - bounds.Top, 0, Math.Max(0, bounds.Height - 1)),
                captureMilliseconds = Math.Round(captureTimer.Elapsed.TotalMilliseconds, 2),
                encodeMilliseconds = Math.Round(encodeTimer.Elapsed.TotalMilliseconds, 2),
                agentFrameMilliseconds = Math.Round(totalTimer.Elapsed.TotalMilliseconds, 2),
                encodedBytes = bytes.Length,
                captureBackend,
                dirtyRectangleCount = dirtyRectangles.Length,
                moveRectangleCount = moveRectangles.Length,
                dirtyPixelRatio = Math.Round(DirtyPixelRatio(dirtyRectangles, bounds.Width, bounds.Height), 6),
                deltaScalePercent = refinement || fullFrame ? 100 : deltaScalePercent,
                refinement,
                accumulatedFrames,
                encoding = encodedAs
            }, Json)), bytes);
    }

    private static SessionResponse VideoFrame(int monitorIndex, int maxWidth, int targetKbps, int targetFps,
        bool forceKeyFrame)
    {
        var payload = VideoFramePayload(monitorIndex, maxWidth, targetKbps, targetFps, forceKeyFrame);
        return payload.Bytes.Length == 0 ? payload.Response :
            payload.Response with { ImageBase64 = Convert.ToBase64String(payload.Bytes) };
    }

    private static SessionVideoPayload VideoFramePayload(int monitorIndex, int maxWidth, int targetKbps,
        int targetFps, bool forceKeyFrame)
    {
        Volatile.Write(ref _lastVideoRequestTimestamp, Stopwatch.GetTimestamp());
        var totalTimer = Stopwatch.StartNew();
        var bounds = CaptureBounds(monitorIndex);
        maxWidth = Math.Clamp(maxWidth, 640, 1920);
        targetKbps = Math.Clamp(targetKbps, 300, 8000);
        targetFps = Math.Clamp(targetFps, 5, 60);
        try
        {
            var outputIndex = monitorIndex < 0
                ? NativeDesktop.PrimaryMonitorIndex()
                : monitorIndex;
            outputIndex = Math.Max(0, outputIndex);
            DxgiH264Frame gpuFrame;
            lock (CaptureSync)
            {
                H264Captures.TryGetValue(outputIndex, out var existingCapture);
                if (existingCapture is null || !existingCapture.Matches(maxWidth, targetKbps, targetFps))
                {
                    H264Captures.Remove(outputIndex);
                    try { existingCapture?.Dispose(); } catch (Exception disposeError) { LogError(disposeError); }
                    CollectReleasedNativeResources();
                    existingCapture = new DxgiH264Capture(outputIndex, maxWidth, targetKbps, targetFps);
                    H264Captures[outputIndex] = existingCapture;
                }
                else if (forceKeyFrame && !existingCapture.RequestKeyFrame())
                {
                    H264Captures.Remove(outputIndex);
                    try { existingCapture.Dispose(); } catch (Exception disposeError) { LogError(disposeError); }
                    CollectReleasedNativeResources();
                    existingCapture = new DxgiH264Capture(outputIndex, maxWidth, targetKbps, targetFps);
                    H264Captures[outputIndex] = existingCapture;
                }
                gpuFrame = existingCapture.Capture(16);
            }
            if (gpuFrame.Bytes.Length == 0)
            {
                if (gpuFrame.CursorChanged)
                {
                    var pointer = NativeDesktop.GetCursorPosition();
                    return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_CURSOR", null,
                        bounds.Width, bounds.Height, JsonSerializer.SerializeToElement(new
                        {
                            sessionId = SessionId, monitorIndex, encodedWidth = gpuFrame.Width,
                            encodedHeight = gpuFrame.Height, fullFrame = false, keyFrame = false,
                            cursorOnly = true,
                            cursorX = Math.Clamp(pointer.X - bounds.Left, 0, Math.Max(0, bounds.Width - 1)),
                            cursorY = Math.Clamp(pointer.Y - bounds.Top, 0, Math.Max(0, bounds.Height - 1)),
                            captureMilliseconds = Math.Round(gpuFrame.CaptureMilliseconds, 2),
                            encodeMilliseconds = 0, encodedBytes = 0,
                            captureBackend = "DXGI_D3D11_CURSOR", encoding = "CURSOR_METADATA"
                        }, Json)), []);
                }
                return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_NO_CHANGE", null,
                    bounds.Width, bounds.Height), []);
            }
            var gpuCursor = NativeDesktop.GetCursorPosition();
            totalTimer.Stop();
            return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_VIDEO_FRAME_OK",
                null, bounds.Width, bounds.Height,
                JsonSerializer.SerializeToElement(new
                {
                    sessionId = SessionId, monitorIndex, encodedWidth = gpuFrame.Width,
                    encodedHeight = gpuFrame.Height, fullFrame = true, keyFrame = gpuFrame.KeyFrame,
                    cursorX = Math.Clamp(gpuCursor.X - bounds.Left, 0, Math.Max(0, bounds.Width - 1)),
                    cursorY = Math.Clamp(gpuCursor.Y - bounds.Top, 0, Math.Max(0, bounds.Height - 1)),
                    captureMilliseconds = Math.Round(gpuFrame.CaptureMilliseconds, 2),
                    encodeMilliseconds = Math.Round(gpuFrame.EncodeMilliseconds, 2),
                    agentFrameMilliseconds = Math.Round(totalTimer.Elapsed.TotalMilliseconds, 2),
                    encodedBytes = gpuFrame.Bytes.Length, captureBackend = "DXGI_D3D11_MF_ZERO_COPY",
                    dirtyRectangleCount = gpuFrame.DirtyRectangleCount,
                    accumulatedFrames = gpuFrame.AccumulatedFrames, encoding = "H264_ANNEX_B"
                }, Json)), gpuFrame.Bytes);
        }
        catch (Exception error)
        {
            LogError(new InvalidOperationException("Zero-copy H.264 fallback activated.", error));
        }
        var scale = Math.Min(1d, Math.Min((double)maxWidth / bounds.Width, 1080d / bounds.Height));
        var width = Math.Max(16, (int)Math.Round(bounds.Width * scale) & ~15);
        var height = Math.Max(16, (int)Math.Round(bounds.Height * scale) & ~15);
        var captureTimer = Stopwatch.StartNew();
        using var captured = CaptureDesktopBitmap(monitorIndex, bounds, 16, borrowDxgiBitmap: false,
            out var backend,
            out var dirty, out _, out var accumulatedFrames);
        var bitmap = captured.Bitmap;
        captureTimer.Stop();
        if (dirty.Length == 0 && accumulatedFrames == 0 && !forceKeyFrame &&
            _h264Encoder?.HasProducedFrame == true)
            return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_NO_CHANGE", null, width, height), []);
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height));
        }
        var encodeTimer = Stopwatch.StartNew();
        if (_h264Encoder is null || !_h264Encoder.Matches(width, height, targetKbps * 1000, targetFps))
        {
            _h264Encoder?.Dispose();
            _h264Encoder = new SessionH264Encoder(width, height, targetKbps * 1000, targetFps);
        }
        else if (forceKeyFrame && !_h264Encoder.RequestKeyFrame())
        {
            _h264Encoder.Dispose();
            _h264Encoder = new SessionH264Encoder(width, height, targetKbps * 1000, targetFps);
        }
        var bytes = _h264Encoder.Encode(scaled);
        encodeTimer.Stop();
        if (bytes.Length == 0) return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_NO_CHANGE", null,
            bounds.Width, bounds.Height, JsonSerializer.SerializeToElement(new {
                encoderInputs = _h264Encoder.InputFrames, encoderOutputs = _h264Encoder.OutputFrames }, Json)), []);
        var cursor = NativeDesktop.GetCursorPosition();
        return new SessionVideoPayload(new SessionResponse(true, "DESKTOP_VIDEO_FRAME_OK", null,
            bounds.Width, bounds.Height,
            JsonSerializer.SerializeToElement(new
            {
                sessionId = SessionId, monitorIndex, encodedWidth = width, encodedHeight = height,
                fullFrame = true, keyFrame = _h264Encoder.LastWasKeyFrame,
                cursorX = Math.Clamp(cursor.X - bounds.Left, 0, Math.Max(0, bounds.Width - 1)),
                cursorY = Math.Clamp(cursor.Y - bounds.Top, 0, Math.Max(0, bounds.Height - 1)),
                captureMilliseconds = Math.Round(captureTimer.Elapsed.TotalMilliseconds, 2),
                encodeMilliseconds = Math.Round(encodeTimer.Elapsed.TotalMilliseconds, 2),
                agentFrameMilliseconds = Math.Round(totalTimer.Elapsed.TotalMilliseconds, 2),
                encodedBytes = bytes.Length, captureBackend = backend,
                dirtyRectangleCount = dirty.Length, accumulatedFrames, encoding = "H264_ANNEX_B"
            }, Json)), bytes);
    }

    private static async Task StreamResourceCleanupLoopAsync()
    {
        while (true)
        {
            await Task.Delay(2000);
            var last = Volatile.Read(ref _lastVideoRequestTimestamp);
            if (last != 0 && Stopwatch.GetElapsedTime(last) >= TimeSpan.FromSeconds(30))
            {
                ReleaseStreamResources();
                Environment.Exit(0);
            }
        }
    }

    private static SessionResponse MarkStreamStopped()
    {
        Volatile.Write(ref _lastVideoRequestTimestamp, Stopwatch.GetTimestamp());
        return new SessionResponse(true, "DESKTOP_STREAM_STOPPED", null, null, null,
            JsonSerializer.SerializeToElement(new { sessionId = SessionId }, Json));
    }

    private static SessionResponse ReleaseStreamResources()
    {
        lock (CaptureSync)
        {
            foreach (var capture in H264Captures.Values)
                try { capture.Dispose(); } catch (Exception error) { LogError(error); }
            H264Captures.Clear();
            foreach (var capture in DxgiCaptures.Values)
                try { capture.Dispose(); } catch (Exception error) { LogError(error); }
            DxgiCaptures.Clear();
            try { _h264Encoder?.Dispose(); } catch (Exception error) { LogError(error); }
            _h264Encoder = null;
            LastFullFrames.Clear();
            PendingRefinementRegions.Clear();
            LastDirtyFrameTimestamps.Clear();
            Volatile.Write(ref _lastVideoRequestTimestamp, 0);
        }
        CollectReleasedNativeResources();
        return new SessionResponse(true, "DESKTOP_STREAM_RELEASED", null, null, null,
            JsonSerializer.SerializeToElement(new { sessionId = SessionId }, Json));
    }

    private static void CollectReleasedNativeResources()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }

    private static string NormalizeImageEncoding(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "png" => "png",
            "jpeg" or "jpg" => "jpeg",
            "webp" => "webp",
            _ => "webp"
        };

    private static string EncodeBitmap(Bitmap bitmap, Stream output, string imageEncoding, int quality)
    {
        imageEncoding = NormalizeImageEncoding(imageEncoding);
        if (imageEncoding == "png")
        {
            bitmap.Save(output, ImageFormat.Png);
            return "PNG";
        }
        if (imageEncoding == "jpeg")
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
                (long)Math.Clamp(quality, 10, 100));
            bitmap.Save(output, JpegEncoder.Value, parameters);
            return "JPEG";
        }

        EncodeWebP(bitmap, output, Math.Clamp(quality, 10, 100));
        return "WEBP";
    }

    private static void EncodeWebP(Bitmap bitmap, Stream output, int quality)
    {
        using var converted = bitmap.PixelFormat == PixelFormat.Format32bppPArgb
            ? null
            : new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppPArgb);
        var source = converted ?? bitmap;
        if (converted is not null)
        {
            using var graphics = Graphics.FromImage(converted);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(bitmap, 0, 0);
        }

        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var locked = source.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = source.Width * 4;
            var pixels = new byte[rowBytes * source.Height];
            for (var y = 0; y < source.Height; y++)
            {
                var row = IntPtr.Add(locked.Scan0, y * locked.Stride);
                Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
            }
            var info = new SKImageInfo(source.Width, source.Height,
                SKColorType.Bgra8888, SKAlphaType.Premul);
            using var skBitmap = new SKBitmap(info);
            Marshal.Copy(pixels, 0, skBitmap.GetPixels(), pixels.Length);
            using var image = SKImage.FromBitmap(skBitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality)
                ?? throw new InvalidOperationException("SkiaSharp WebP encoder returned no data.");
            encoded.SaveTo(output);
        }
        finally
        {
            source.UnlockBits(locked);
        }
    }

    private static int RunImageCodecSelfTest()
    {
        using var bitmap = new Bitmap(96, 64, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            graphics.FillRectangle(brush, 4, 4, 40, 20);
            graphics.DrawString("SIRK", SystemFonts.DefaultFont, brush, 4, 32);
        }

        foreach (var codec in new[] { "jpeg", "png", "webp" })
        {
            using var output = new MemoryStream();
            var encodedAs = EncodeBitmap(bitmap, output, codec, 85);
            var bytes = output.ToArray();
            var valid = encodedAs switch
            {
                "JPEG" => bytes.Length > 2 && bytes[0] == 0xff && bytes[1] == 0xd8,
                "PNG" => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 &&
                         bytes[2] == 0x4e && bytes[3] == 0x47,
                "WEBP" => bytes.Length > 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
                          Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
                _ => false
            };
            if (!valid) return codec switch { "jpeg" => 21, "png" => 22, _ => 23 };
        }
        return 0;
    }

    private static bool DirtyRegionsRequireFullFrame(Rectangle[] dirtyRectangles, Rectangle bounds)
    {
        var regions = MergeDirtyRectangles(dirtyRectangles, bounds);
        if (regions.Count > 64) regions = CoalesceToGrid(regions, bounds, 8, 8);
        if (regions.Count == 0) return true;
        var dirtyArea = regions.Sum(value => (long)value.Width * value.Height);
        return dirtyArea >= (long)bounds.Width * bounds.Height * 7 / 10;
    }

    private static Bitmap BuildEncodedBitmap(Bitmap source, Rectangle bounds, Rectangle[] dirtyRectangles,
        double scale, bool forceFull, out bool fullFrame, out DesktopPatch[] patches)
    {
        var regions = MergeDirtyRectangles(dirtyRectangles, bounds);
        if (regions.Count > 64) regions = CoalesceToGrid(regions, bounds, 8, 8);
        var dirtyArea = regions.Sum(value => (long)value.Width * value.Height);
        fullFrame = forceFull || regions.Count == 0 ||
                    dirtyArea >= (long)bounds.Width * bounds.Height * 7 / 10;
        if (fullFrame)
        {
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            patches =
            [
                new DesktopPatch(0, 0, width, height,
                    0, 0, bounds.Width, bounds.Height)
            ];
            var full = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(full);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
            return full;
        }

        var scaled = regions.Select(region => new
        {
            Destination = region,
            Source = region,
            Width = Math.Max(1, (int)Math.Ceiling(region.Width * scale)),
            Height = Math.Max(1, (int)Math.Ceiling(region.Height * scale))
        }).Where(value => value.Source.Width > 0 && value.Source.Height > 0)
          .OrderByDescending(value => value.Height).ToArray();
        var atlasLimit = Math.Max(1024, scaled.Max(value => value.Width));
        var placements = new List<(Rectangle Atlas, Rectangle Source, Rectangle Destination)>();
        var x = 0;
        var y = 0;
        var rowHeight = 0;
        var atlasWidth = 0;
        foreach (var item in scaled)
        {
            if (x > 0 && x + item.Width > atlasLimit)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }
            var atlas = new Rectangle(x, y, item.Width, item.Height);
            placements.Add((atlas, item.Source, item.Destination));
            x += item.Width;
            rowHeight = Math.Max(rowHeight, item.Height);
            atlasWidth = Math.Max(atlasWidth, x);
        }
        var atlasHeight = y + rowHeight;
        var bitmap = new Bitmap(Math.Max(1, atlasWidth), Math.Max(1, atlasHeight),
            PixelFormat.Format32bppArgb);
        CopyBitmapRegions(source, bitmap, placements);
        patches = placements.Select(value => new DesktopPatch(
            value.Atlas.X, value.Atlas.Y, value.Atlas.Width, value.Atlas.Height,
            value.Destination.X, value.Destination.Y, value.Destination.Width, value.Destination.Height)).ToArray();
        return bitmap;
    }

    private static unsafe void CopyBitmapRegions(Bitmap source, Bitmap destination,
        IReadOnlyList<(Rectangle Atlas, Rectangle Source, Rectangle Destination)> placements)
    {
        if (source.PixelFormat != PixelFormat.Format32bppArgb)
        {
            using var graphics = Graphics.FromImage(destination);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            foreach (var placement in placements)
                graphics.DrawImage(source, placement.Atlas, placement.Source, GraphicsUnit.Pixel);
            return;
        }
        var sourceData = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var destinationData = destination.LockBits(new Rectangle(0, 0, destination.Width, destination.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                foreach (var placement in placements)
                {
                    var sourceRegion = placement.Source;
                    var destinationRegion = placement.Atlas;
                    if (sourceRegion.Size == destinationRegion.Size)
                    {
                        var bytes = sourceRegion.Width * 4;
                        for (var row = 0; row < sourceRegion.Height; row++)
                            CopyMemory(
                                IntPtr.Add(destinationData.Scan0,
                                    (destinationRegion.Y + row) * destinationData.Stride + destinationRegion.X * 4),
                                IntPtr.Add(sourceData.Scan0,
                                    (sourceRegion.Y + row) * sourceData.Stride + sourceRegion.X * 4),
                                (nuint)bytes);
                    }
                    else
                    {
                        for (var y = 0; y < destinationRegion.Height; y++)
                        {
                            var sourceY = sourceRegion.Y + y * sourceRegion.Height / destinationRegion.Height;
                            var sourceRow = (uint*)IntPtr.Add(sourceData.Scan0,
                                sourceY * sourceData.Stride + sourceRegion.X * 4).ToPointer();
                            var destinationRow = (uint*)IntPtr.Add(destinationData.Scan0,
                                (destinationRegion.Y + y) * destinationData.Stride + destinationRegion.X * 4).ToPointer();
                            for (var x = 0; x < destinationRegion.Width; x++)
                                destinationRow[x] = sourceRow[x * sourceRegion.Width / destinationRegion.Width];
                        }
                    }
                }
            }
            finally { destination.UnlockBits(destinationData); }
        }
        finally { source.UnlockBits(sourceData); }
    }

    private static List<Rectangle> MergeDirtyRectangles(IEnumerable<Rectangle> values, Rectangle bounds)
    {
        var regions = new List<Rectangle>();
        foreach (var original in values)
        {
            var candidate = original;
            candidate.Intersect(new Rectangle(0, 0, bounds.Width, bounds.Height));
            if (candidate.Width <= 0 || candidate.Height <= 0) continue;
            candidate.Inflate(2, 2);
            candidate.Intersect(new Rectangle(0, 0, bounds.Width, bounds.Height));
            for (var index = regions.Count - 1; index >= 0; index--)
            {
                var expanded = regions[index];
                expanded.Inflate(8, 8);
                if (!expanded.IntersectsWith(candidate)) continue;
                candidate = Rectangle.Union(candidate, regions[index]);
                regions.RemoveAt(index);
            }
            regions.Add(candidate);
        }
        return regions;
    }

    private static List<Rectangle> CoalesceToGrid(IEnumerable<Rectangle> values, Rectangle bounds,
        int columns, int rows)
    {
        var cells = new Dictionary<(int X, int Y), Rectangle>();
        foreach (var value in values)
        {
            var centerX = Math.Clamp(value.Left + value.Width / 2, 0, Math.Max(0, bounds.Width - 1));
            var centerY = Math.Clamp(value.Top + value.Height / 2, 0, Math.Max(0, bounds.Height - 1));
            var key = (
                Math.Min(columns - 1, centerX * columns / Math.Max(1, bounds.Width)),
                Math.Min(rows - 1, centerY * rows / Math.Max(1, bounds.Height)));
            cells[key] = cells.TryGetValue(key, out var existing)
                ? Rectangle.Union(existing, value)
                : value;
        }
        return cells.Values.ToList();
    }

    private static CapturedBitmap CaptureDesktopBitmap(int monitorIndex, Rectangle bounds, int timeoutMilliseconds,
        bool borrowDxgiBitmap,
        out string backend, out Rectangle[] dirtyRectangles, out DesktopMove[] moveRectangles,
        out uint accumulatedFrames)
    {
        try
        {
            var outputIndex = monitorIndex < 0
                ? NativeDesktop.PrimaryMonitorIndex()
                : monitorIndex;
            outputIndex = Math.Max(0, outputIndex);
            DxgiFrame frame;
            lock (CaptureSync)
            {
                if (!DxgiCaptures.TryGetValue(outputIndex, out var capture))
                {
                    capture = new DxgiDesktopCapture(outputIndex);
                    DxgiCaptures[outputIndex] = capture;
                }
                frame = capture.Capture((uint)Math.Clamp(timeoutMilliseconds, 1, 100), !borrowDxgiBitmap);
            }
            backend = "DXGI_DESKTOP_DUPLICATION";
            dirtyRectangles = frame.DirtyRectangles;
            moveRectangles = frame.MoveRectangles;
            accumulatedFrames = frame.AccumulatedFrames;
            return new CapturedBitmap(frame.Bitmap, frame.OwnsBitmap);
        }
        catch (Exception error)
        {
            backend = "GDI_STRETCHBLT_FALLBACK:" + error.GetType().Name;
            dirtyRectangles = [new Rectangle(0, 0, bounds.Width, bounds.Height)];
            moveRectangles = [];
            accumulatedFrames = 1;
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(bitmap);
            var destination = graphics.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                _ = SetStretchBltMode(destination, 3);
                if (screen == IntPtr.Zero || !StretchBlt(destination, 0, 0, bitmap.Width, bitmap.Height,
                        screen, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x00CC0020))
                    throw new AggregateException("DXGI and GDI capture failed.", error,
                        new Win32Exception(Marshal.GetLastWin32Error()));
            }
            finally
            {
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                graphics.ReleaseHdc(destination);
            }
            return new CapturedBitmap(bitmap, true);
        }
    }

    private static double DirtyPixelRatio(IEnumerable<Rectangle> rectangles, int width, int height)
    {
        var total = Math.Max(1L, (long)width * height);
        var dirty = rectangles.Sum(value => Math.Max(0L, (long)value.Width * value.Height));
        return Math.Min(1d, (double)dirty / total);
    }

    private static SessionResponse Input(SessionRequest request)
    {
        if (request.Action == "clipboardSet")
        {
            var text = request.Text ?? "";
            if (text.Length > 1024 * 1024) throw new InvalidDataException("Schowek przekracza limit 1 MiB.");
            if (text.Length == 0) NativeClipboard.Clear();
            else NativeClipboard.SetText(text);
            return new SessionResponse(true, "DESKTOP_CLIPBOARD_SET_OK", null, null, null);
        }
        if (request.Action == "clipboardGet")
        {
            var clipboard = NativeClipboard.Read();
            return new SessionResponse(true, "DESKTOP_CLIPBOARD_GET_OK", null, null, null,
                JsonSerializer.SerializeToElement(clipboard, Json));
        }
        if (request.Action == "clipboardFileSet")
        {
            var fileName = Path.GetFileName(request.FileName ?? "");
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidDataException("Nazwa pliku jest nieprawidłowa.");
            byte[] content;
            try { content = Convert.FromBase64String(request.FileBase64 ?? ""); }
            catch (FormatException) { throw new InvalidDataException("Zawartość pliku nie jest Base64."); }
            if (content.Length > 512 * 1024)
                throw new InvalidDataException("Transfer schowka przekracza limit 512 KiB.");
            var directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "SIRK", "Transfers");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, content);
            NativeClipboard.SetFileDrop(path);
            return new SessionResponse(true, "DESKTOP_CLIPBOARD_FILE_SET_OK", null, null, null,
                JsonSerializer.SerializeToElement(new { fileName, path, bytes = content.Length }, Json));
        }
        if (request.Action == "text")
        {
            NativeInput.SendText(request.Text ?? string.Empty);
            return new SessionResponse(true, "DESKTOP_TEXT_OK", null, null, null);
        }
        if (request.Action == "key")
        {
            NativeInput.SendKey(request.Key, request.Modifiers);
            return new SessionResponse(true, "DESKTOP_KEY_OK", null, null, null);
        }

        var allowedMouseActions = new[] { "move", "click", "doubleClick", "rightClick", "middleClick",
            "leftDown", "leftUp", "rightDown", "rightUp", "wheel" };
        if (!allowedMouseActions.Contains(request.Action, StringComparer.Ordinal))
            throw new InvalidDataException("Niedozwolona operacja myszy.");
        var bounds = CaptureBounds(request.MonitorIndex ?? -1);
        var x = Math.Clamp(request.X ?? 0, 0, Math.Max(0, bounds.Width - 1)) + bounds.Left;
        var y = Math.Clamp(request.Y ?? 0, 0, Math.Max(0, bounds.Height - 1)) + bounds.Top;
        var previous = NativeDesktop.GetCursorPosition();
        NativeDesktop.SetCursorPosition(new Point(x, y));
        if (request.Action is "click" or "doubleClick")
        {
            var count = request.Action == "doubleClick" ? 2 : 1;
            for (var index = 0; index < count; index++)
            {
                MouseEvent(MouseEventLeftDown);
                MouseEvent(MouseEventLeftUp);
            }
        }
        else if (request.Action == "rightClick")
        {
            MouseEvent(MouseEventRightDown);
            MouseEvent(MouseEventRightUp);
        }
        else if (request.Action == "middleClick")
        {
            MouseEvent(MouseEventMiddleDown);
            MouseEvent(MouseEventMiddleUp);
        }
        else if (request.Action == "leftDown") MouseEvent(MouseEventLeftDown);
        else if (request.Action == "leftUp") MouseEvent(MouseEventLeftUp);
        else if (request.Action == "rightDown") MouseEvent(MouseEventRightDown);
        else if (request.Action == "rightUp") MouseEvent(MouseEventRightUp);
        else if (request.Action == "wheel")
            mouse_event(MouseEventWheel, 0, 0, unchecked((uint)(request.Delta ?? 0)), UIntPtr.Zero);
        return new SessionResponse(true, "DESKTOP_INPUT_OK", null, bounds.Width, bounds.Height,
            JsonSerializer.SerializeToElement(new
            {
                sessionId = SessionId,
                action = request.Action,
                x = x - bounds.Left,
                y = y - bounds.Top,
                previousX = previous.X - bounds.Left,
                previousY = previous.Y - bounds.Top
            }, Json));
    }

    private static SessionResponse Activity()
    {
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var processId);
        var title = new StringBuilder(1024);
        _ = GetWindowText(foreground, title, title.Capacity);
        string? processName = null;
        try { processName = Process.GetProcessById((int)processId).ProcessName; } catch { }

        var lastInput = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        var idleMilliseconds = GetLastInputInfo(ref lastInput)
            ? unchecked((uint)Environment.TickCount - lastInput.Time) : 0;
        var clipboard = NativeClipboard.Metadata();
        var now = DateTimeOffset.UtcNow;
        var cursor = NativeDesktop.GetCursorPosition();
        var previousCursor = _lastCursorPosition;
        var cursorDistance = previousCursor is null ? 0d : Math.Sqrt(
            Math.Pow(cursor.X - previousCursor.Value.X, 2) +
            Math.Pow(cursor.Y - previousCursor.Value.Y, 2));
        var keyboard = new byte[256];
        var pressedKeyCount = GetKeyboardState(keyboard)
            ? keyboard.Count(value => (value & 0x80) != 0) : 0;
        var sampleIntervalMs = Math.Max(0, (now - _lastActivitySampleUtc).TotalMilliseconds);
        _lastActivitySampleUtc = now;
        _lastCursorPosition = cursor;
        var uiAutomation = NativeDesktop.WindowMetadata(foreground);
        var data = JsonSerializer.SerializeToElement(new
        {
            sessionId = Process.GetCurrentProcess().SessionId,
            userSid = WindowsIdentity.GetCurrent().User?.Value,
            foregroundProcess = processName,
            foregroundWindowTitle = Limit(title.ToString(), 512),
            idleSeconds = idleMilliseconds / 1000,
            clipboard,
            inputTiming = new
            {
                sampleIntervalMs = Math.Round(sampleIntervalMs),
                pressedKeyCount,
                cursorDistancePixels = Math.Round(cursorDistance, 2),
                cursorX = cursor.X,
                cursorY = cursor.Y,
                capturesCharacters = false,
                capturesKeyCodes = false
            },
            uiAutomation
        }, Json);
        return new SessionResponse(true, "ACTIVITY_SNAPSHOT_OK", null, null, null, data);
    }

    private static string? Limit(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];

    private static void MouseEvent(uint flags) => mouse_event(flags, 0, 0, 0, UIntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr deviceContext, int mode);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);
}

internal sealed record CapturedBitmap(Bitmap Bitmap, bool OwnsBitmap) : IDisposable
{
    public void Dispose() { if (OwnsBitmap) Bitmap.Dispose(); }
}

internal sealed record SessionRequest(string Type, string? Action, int? X, int? Y, int? Delta, int? MonitorIndex,
    int? MaxWidth, int? Quality, int? TargetKbps, int? TargetFps, int? DeltaScalePercent,
    string? ImageEncoding, string? Text, string? Key, string? Modifiers,
    string? FileName, string? FileBase64, bool? ForceFull);
internal sealed record SessionResponse(bool Ok, string Code, string? ImageBase64, int? Width, int? Height,
    JsonElement? Data = null, string? Error = null);
internal sealed record SessionVideoPayload(SessionResponse Response, byte[] Bytes);
internal sealed record DesktopPatch(int AtlasX, int AtlasY, int AtlasWidth, int AtlasHeight,
    int X, int Y, int Width, int Height);

[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint Size;
    public uint Time;
}
