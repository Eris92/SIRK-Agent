using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

const int MaximumControlMessageBytes = 64 * 1024;
const int MaximumFrameBytes = 16 * 1024 * 1024;

WorkspaceHostOptions? options = WorkspaceHostOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: SIRK-WorkspaceHost --session-id <id> --pipe-name <name> --token <base64url>");
    return 2;
}

if (!NativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out uint currentSessionId))
{
    Console.Error.WriteLine("Unable to resolve the current Windows session.");
    return 3;
}

if (currentSessionId == 0 || currentSessionId != options.SessionId)
{
    Console.Error.WriteLine("WorkspaceHost session validation failed.");
    return 4;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

try
{
    await using var pipe = new NamedPipeClientStream(
        ".",
        options.PipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous,
        TokenImpersonationLevel.Identification);

    await pipe.ConnectAsync(timeout.Token);

    byte[] hello = JsonSerializer.SerializeToUtf8Bytes(new
    {
        protocolVersion = 1,
        messageType = "WorkspaceHost.Hello",
        sessionId = options.SessionId,
        processId = Environment.ProcessId,
        token = options.Token
    });

    await Framing.WriteFrameAsync(pipe, hello, MaximumControlMessageBytes, timeout.Token);
    byte[] acknowledgement = await Framing.ReadFrameAsync(pipe, MaximumControlMessageBytes, timeout.Token);

    using (JsonDocument response = JsonDocument.Parse(acknowledgement))
    {
        if (!response.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
        {
            Console.Error.WriteLine("WorkspaceHost handshake was rejected.");
            return 5;
        }
    }

    byte[] commandBytes = await Framing.ReadFrameAsync(pipe, MaximumControlMessageBytes, timeout.Token);
    WorkspaceHostCommand? command = JsonSerializer.Deserialize<WorkspaceHostCommand>(commandBytes, JsonOptions.Value);
    if (command?.MessageType != "WorkspaceHost.CaptureFrame" || command.Request is null)
    {
        await Framing.SendErrorAsync(pipe, "invalid_command", "Unsupported or malformed WorkspaceHost command.", timeout.Token);
        return 9;
    }

    CaptureResult capture = DesktopCapture.Capture(command.Request);
    if (!capture.Success)
    {
        await Framing.SendErrorAsync(pipe, capture.ErrorCode ?? "capture_failed", capture.ErrorMessage ?? "Desktop capture failed.", timeout.Token);
        return 10;
    }

    byte[] result = JsonSerializer.SerializeToUtf8Bytes(new
    {
        ok = true,
        contentType = "image/jpeg",
        width = capture.Width,
        height = capture.Height,
        frameBase64 = Convert.ToBase64String(capture.Bytes!)
    }, JsonOptions.Value);

    await Framing.WriteFrameAsync(pipe, result, MaximumFrameBytes, timeout.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("WorkspaceHost operation timed out.");
    return 6;
}
catch (IOException)
{
    Console.Error.WriteLine("WorkspaceHost IPC is unavailable.");
    return 7;
}
catch (JsonException)
{
    Console.Error.WriteLine("WorkspaceHost received invalid JSON.");
    return 8;
}
catch (ExternalException)
{
    Console.Error.WriteLine("WorkspaceHost desktop capture failed.");
    return 10;
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Value = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };
}

internal static class Framing
{
    internal static async Task SendErrorAsync(Stream stream, string code, string message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { ok = false, error = new { code, message } }, JsonOptions.Value);
        await WriteFrameAsync(stream, payload, MaximumControlMessageBytes, cancellationToken);
    }

    internal static async Task WriteFrameAsync(Stream stream, byte[] payload, int maximumBytes, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 || payload.Length > maximumBytes)
        {
            throw new InvalidDataException("WorkspaceHost message length is outside the allowed limit.");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<byte[]> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException("WorkspaceHost response length is outside the allowed limit.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}

internal sealed record WorkspaceHostCommand
{
    [JsonPropertyName("messageType")]
    public string MessageType { get; init; } = string.Empty;

    [JsonPropertyName("request")]
    public CaptureRequest? Request { get; init; }
}

internal sealed record CaptureRequest
{
    [JsonPropertyName("monitorId")]
    public string MonitorId { get; init; } = "primary";

    [JsonPropertyName("quality")]
    public int Quality { get; init; } = 70;

    [JsonPropertyName("maxWidth")]
    public int MaxWidth { get; init; } = 1920;

    [JsonPropertyName("maxHeight")]
    public int MaxHeight { get; init; } = 1080;

    [JsonPropertyName("includeCursor")]
    public bool IncludeCursor { get; init; } = true;
}

internal sealed record CaptureResult(bool Success, byte[]? Bytes, int Width, int Height, string? ErrorCode, string? ErrorMessage)
{
    internal static CaptureResult Failure(string code, string message) => new(false, null, 0, 0, code, message);
}

internal static class DesktopCapture
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;

    internal static CaptureResult Capture(CaptureRequest request)
    {
        if (request.Quality is < 20 or > 95 || request.MaxWidth is < 320 or > 7680 || request.MaxHeight is < 200 or > 4320)
        {
            return CaptureResult.Failure("invalid_capture_request", "Capture dimensions or JPEG quality are outside allowed limits.");
        }

        bool allMonitors = string.Equals(request.MonitorId, "all", StringComparison.Ordinal);
        if (!allMonitors && !string.Equals(request.MonitorId, "primary", StringComparison.Ordinal))
        {
            return CaptureResult.Failure("monitor_not_found", "The first capture provider supports monitorId primary or all.");
        }

        int left = allMonitors ? NativeMethods.GetSystemMetrics(SmXVirtualScreen) : 0;
        int top = allMonitors ? NativeMethods.GetSystemMetrics(SmYVirtualScreen) : 0;
        int width = NativeMethods.GetSystemMetrics(allMonitors ? SmCxVirtualScreen : SmCxScreen);
        int height = NativeMethods.GetSystemMetrics(allMonitors ? SmCyVirtualScreen : SmCyScreen);
        if (width <= 0 || height <= 0)
        {
            return CaptureResult.Failure("desktop_unavailable", "Windows returned an invalid desktop size.");
        }

        using var source = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            if (request.IncludeCursor)
            {
                DrawCursor(graphics, left, top);
            }
        }

        Size targetSize = Fit(width, height, request.MaxWidth, request.MaxHeight);
        using Bitmap output = targetSize.Width == width && targetSize.Height == height ? new Bitmap(source) : Resize(source, targetSize);
        using var memory = new MemoryStream();
        ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, request.Quality);
        output.Save(memory, jpegCodec, encoderParameters);
        return new CaptureResult(true, memory.ToArray(), output.Width, output.Height, null, null);
    }

    private static Size Fit(int width, int height, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(1d, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static Bitmap Resize(Bitmap source, Size target)
    {
        var bitmap = new Bitmap(target.Width, target.Height, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.DrawImage(source, new Rectangle(Point.Empty, target));
        return bitmap;
    }

    private static void DrawCursor(Graphics graphics, int captureLeft, int captureTop)
    {
        var cursorInfo = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref cursorInfo) || (cursorInfo.flags & CursorShowing) == 0 || cursorInfo.hCursor == IntPtr.Zero)
        {
            return;
        }

        IntPtr deviceContext = graphics.GetHdc();
        try
        {
            _ = NativeMethods.DrawIconEx(deviceContext, cursorInfo.ptScreenPos.X - captureLeft, cursorInfo.ptScreenPos.Y - captureTop, cursorInfo.hCursor, 0, 0, 0, IntPtr.Zero, DiNormal);
        }
        finally
        {
            graphics.ReleaseHdc(deviceContext);
        }
    }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorInfo(ref CURSORINFO cursorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        internal int cbSize;
        internal int flags;
        internal IntPtr hCursor;
        internal POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }
}

internal sealed record WorkspaceHostOptions(uint SessionId, string PipeName, string Token)
{
    internal static WorkspaceHostOptions? Parse(string[] arguments)
    {
        if (arguments.Length != 6)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(arguments[index], arguments[index + 1]))
            {
                return null;
            }
        }

        if (!values.TryGetValue("--session-id", out string? sessionValue) || !uint.TryParse(sessionValue, out uint sessionId) || sessionId == 0)
        {
            return null;
        }

        if (!values.TryGetValue("--pipe-name", out string? pipeName) || string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128 || pipeName.Contains('\\') || pipeName.Contains('/'))
        {
            return null;
        }

        if (!values.TryGetValue("--token", out string? token) || !IsValidToken(token))
        {
            return null;
        }

        return new WorkspaceHostOptions(sessionId, pipeName, token);
    }

    private static bool IsValidToken(string token) => token.Length is >= 43 and <= 128 && token.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
}