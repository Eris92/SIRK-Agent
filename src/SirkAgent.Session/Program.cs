using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Automation;

namespace SirkAgent.Session;

internal static class Program
{
    private const string PipeName = "SIRK-Agent-Interactive-Session";
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static DateTimeOffset _lastActivitySampleUtc = DateTimeOffset.UtcNow;
    private static System.Drawing.Point? _lastCursorPosition;

    [STAThread]
    private static async Task Main()
    {
        while (true)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync();
                if (!Authorized(pipe))
                {
                    pipe.Disconnect();
                    continue;
                }
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                    { AutoFlush = true };
                var line = await reader.ReadLineAsync();
                var request = JsonSerializer.Deserialize<SessionRequest>(line ?? "{}", Json);
                var response = request?.Type switch
                {
                    "snapshot" => Snapshot(),
                    "mouse" => Mouse(request),
                    "activity" => Activity(),
                    _ => new SessionResponse(false, "SESSION_REQUEST_INVALID", null, null, null)
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json));
            }
            catch (Exception error)
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
                await Task.Delay(1000);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
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
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
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

    private static SessionResponse Snapshot()
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Aktywna sesja nie udostępnia ekranu.");
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            var destination = graphics.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                if (screen == IntPtr.Zero || !BitBlt(destination, 0, 0, bounds.Width, bounds.Height,
                        screen, bounds.Left, bounds.Top, 0x00CC0020))
                    throw new InvalidOperationException("Nie udało się przechwycić aktywnego pulpitu.");
            }
            finally
            {
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                graphics.ReleaseHdc(destination);
            }
        }
        var scale = Math.Min(1d, Math.Min(1600d / bounds.Width, 900d / bounds.Height));
        using var bitmap = scale < 1d
            ? new Bitmap(source, new Size((int)(bounds.Width * scale), (int)(bounds.Height * scale)))
            : new Bitmap(source);
        using var output = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(value => value.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
        bitmap.Save(output, encoder, parameters);
        return new SessionResponse(true, "DESKTOP_SNAPSHOT_OK", Convert.ToBase64String(output.ToArray()),
            bounds.Width, bounds.Height);
    }

    private static SessionResponse Mouse(SessionRequest request)
    {
        var bounds = SystemInformation.VirtualScreen;
        var x = Math.Clamp(request.X ?? 0, 0, Math.Max(0, bounds.Width - 1)) + bounds.Left;
        var y = Math.Clamp(request.Y ?? 0, 0, Math.Max(0, bounds.Height - 1)) + bounds.Top;
        Cursor.Position = new Point(x, y);
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
        return new SessionResponse(true, "DESKTOP_INPUT_OK", null, bounds.Width, bounds.Height);
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
        var clipboard = ClipboardMetadata();
        var now = DateTimeOffset.UtcNow;
        var cursor = Cursor.Position;
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
        var uiAutomation = UiAutomation(foreground);
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

    private static object? UiAutomation(IntPtr foreground)
    {
        try
        {
            var element = AutomationElement.FromHandle(foreground);
            var current = element.Current;
            return new
            {
                name = Limit(current.Name, 512),
                automationId = Limit(current.AutomationId, 256),
                controlType = Limit(current.ControlType?.ProgrammaticName, 128),
                className = Limit(current.ClassName, 256),
                frameworkId = Limit(current.FrameworkId, 128),
                bounds = new
                {
                    x = current.BoundingRectangle.X,
                    y = current.BoundingRectangle.Y,
                    width = current.BoundingRectangle.Width,
                    height = current.BoundingRectangle.Height
                }
            };
        }
        catch (Exception error)
        {
            return new { available = false, error = error.GetType().Name };
        }
    }

    private static object ClipboardMetadata()
    {
        try
        {
            var formats = Clipboard.GetDataObject()?.GetFormats(autoConvert: false)
                .Take(32).ToArray() ?? [];
            var fileCount = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Count : 0;
            var textLength = Clipboard.ContainsText() ? Clipboard.GetText().Length : 0;
            return new { available = true, formats, fileCount, textLength };
        }
        catch (Exception error)
        {
            return new { available = false, error = error.GetType().Name };
        }
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
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
}

internal sealed record SessionRequest(string Type, string? Action, int? X, int? Y);
internal sealed record SessionResponse(bool Ok, string Code, string? ImageBase64, int? Width, int? Height,
    JsonElement? Data = null);

[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint Size;
    public uint Time;
}
