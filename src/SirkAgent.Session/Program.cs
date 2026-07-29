using System.Drawing.Imaging;
using System.Collections.Specialized;
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
    private static readonly int SessionId = Process.GetCurrentProcess().SessionId;
    private static readonly string PipeName = "SIRK-Agent-Interactive-Session-" + SessionId;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static DateTimeOffset _lastActivitySampleUtc = DateTimeOffset.UtcNow;
    private static System.Drawing.Point? _lastCursorPosition;

    [STAThread]
    private static async Task Main()
    {
        using var singleInstance = new Mutex(true, "Local\\SIRK-Agent-Interactive-Session-" + SessionId,
            out var ownsMutex);
        if (!ownsMutex) return;
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
                                request.Quality ?? 40),
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
            catch (Exception error)
            {
                LogError(error);
                await Task.Delay(1000);
            }
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

    private static SessionResponse Monitors()
    {
        var monitors = Screen.AllScreens.Select((screen, index) => new
        {
            index,
            name = screen.DeviceName,
            primary = screen.Primary,
            x = screen.Bounds.X,
            y = screen.Bounds.Y,
            width = screen.Bounds.Width,
            height = screen.Bounds.Height
        }).ToArray();
        return new SessionResponse(true, "DESKTOP_MONITORS_OK", null, null, null,
            JsonSerializer.SerializeToElement(new { sessionId = SessionId, monitors }, Json));
    }

    private static Rectangle CaptureBounds(int monitorIndex)
    {
        if (monitorIndex < 0) return SystemInformation.VirtualScreen;
        var screens = Screen.AllScreens;
        if (monitorIndex >= screens.Length) throw new InvalidDataException("Wybrany monitor nie istnieje.");
        return screens[monitorIndex].Bounds;
    }

    private static SessionResponse Snapshot(int monitorIndex, int maxWidth, int quality)
    {
        var totalTimer = Stopwatch.StartNew();
        var bounds = CaptureBounds(monitorIndex);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Aktywna sesja nie udostępnia ekranu.");
        maxWidth = Math.Clamp(maxWidth, 640, 1920);
        quality = Math.Clamp(quality, 25, 80);
        var scale = Math.Min(1d, Math.Min((double)maxWidth / bounds.Width, 1080d / bounds.Height));
        using var bitmap = new Bitmap((int)(bounds.Width * scale), (int)(bounds.Height * scale),
            PixelFormat.Format24bppRgb);
        var captureTimer = Stopwatch.StartNew();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var destination = graphics.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                _ = SetStretchBltMode(destination, 3);
                if (screen == IntPtr.Zero || !StretchBlt(destination, 0, 0, bitmap.Width, bitmap.Height,
                        screen, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x00CC0020))
                    throw new InvalidOperationException("Nie udało się przechwycić aktywnego pulpitu.");
            }
            finally
            {
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
                graphics.ReleaseHdc(destination);
            }
        }
        captureTimer.Stop();
        var encodeTimer = Stopwatch.StartNew();
        using var output = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(value => value.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(output, encoder, parameters);
        encodeTimer.Stop();
        var bytes = output.ToArray();
        totalTimer.Stop();
        var cursor = Cursor.Position;
        return new SessionResponse(true, "DESKTOP_SNAPSHOT_OK", Convert.ToBase64String(bytes),
            bounds.Width, bounds.Height, JsonSerializer.SerializeToElement(new
            {
                sessionId = SessionId,
                monitorIndex,
                encodedWidth = bitmap.Width,
                encodedHeight = bitmap.Height,
                originX = bounds.Left,
                originY = bounds.Top,
                cursorX = Math.Clamp(cursor.X - bounds.Left, 0, Math.Max(0, bounds.Width - 1)),
                cursorY = Math.Clamp(cursor.Y - bounds.Top, 0, Math.Max(0, bounds.Height - 1)),
                captureMilliseconds = Math.Round(captureTimer.Elapsed.TotalMilliseconds, 2),
                encodeMilliseconds = Math.Round(encodeTimer.Elapsed.TotalMilliseconds, 2),
                agentFrameMilliseconds = Math.Round(totalTimer.Elapsed.TotalMilliseconds, 2),
                encodedBytes = bytes.Length,
                captureBackend = "GDI_STRETCHBLT",
                encoding = "JPEG"
            }, Json));
    }

    private static SessionResponse Input(SessionRequest request)
    {
        if (request.Action == "clipboardSet")
        {
            var text = request.Text ?? "";
            if (text.Length > 1024 * 1024) throw new InvalidDataException("Schowek przekracza limit 1 MiB.");
            RunSta(() =>
            {
                if (text.Length == 0) Clipboard.Clear();
                else Clipboard.SetText(text);
                return true;
            });
            return new SessionResponse(true, "DESKTOP_CLIPBOARD_SET_OK", null, null, null);
        }
        if (request.Action == "clipboardGet")
        {
            var clipboard = RunSta(ReadClipboard);
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
            RunSta(() =>
            {
                Clipboard.SetFileDropList(new StringCollection { path });
                return true;
            });
            return new SessionResponse(true, "DESKTOP_CLIPBOARD_FILE_SET_OK", null, null, null,
                JsonSerializer.SerializeToElement(new { fileName, path, bytes = content.Length }, Json));
        }
        if (request.Action == "text")
        {
            RunSta(() => { SendKeys.SendWait(EscapeSendKeys(request.Text ?? "")); return true; });
            return new SessionResponse(true, "DESKTOP_TEXT_OK", null, null, null);
        }
        if (request.Action == "key")
        {
            RunSta(() => { SendKeys.SendWait(KeySequence(request.Key, request.Modifiers)); return true; });
            return new SessionResponse(true, "DESKTOP_KEY_OK", null, null, null);
        }

        var allowedMouseActions = new[] { "move", "click", "doubleClick", "rightClick", "middleClick",
            "leftDown", "leftUp", "rightDown", "rightUp", "wheel" };
        if (!allowedMouseActions.Contains(request.Action, StringComparer.Ordinal))
            throw new InvalidDataException("Niedozwolona operacja myszy.");
        var bounds = CaptureBounds(request.MonitorIndex ?? -1);
        var x = Math.Clamp(request.X ?? 0, 0, Math.Max(0, bounds.Width - 1)) + bounds.Left;
        var y = Math.Clamp(request.Y ?? 0, 0, Math.Max(0, bounds.Height - 1)) + bounds.Top;
        var previous = Cursor.Position;
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

    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        })
        {
            IsBackground = true,
            Name = "SIRK Agent interactive STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Operacja sesji interaktywnej przekroczyła limit czasu.");
        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
        return result!;
    }

    private static string EscapeSendKeys(string value) => value
        .Replace("{", "{{}").Replace("}", "{}}")
        .Replace("+", "{+}").Replace("^", "{^}").Replace("%", "{%}").Replace("~", "{~}");

    private static string KeySequence(string? key, string? modifiers)
    {
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = "{ENTER}", ["Tab"] = "{TAB}", ["Escape"] = "{ESC}", ["Backspace"] = "{BS}",
            ["Delete"] = "{DEL}", ["Up"] = "{UP}", ["Down"] = "{DOWN}", ["Left"] = "{LEFT}",
            ["Right"] = "{RIGHT}", ["Home"] = "{HOME}", ["End"] = "{END}", ["PageUp"] = "{PGUP}",
            ["PageDown"] = "{PGDN}", ["F1"] = "{F1}", ["F2"] = "{F2}", ["F3"] = "{F3}",
            ["F4"] = "{F4}", ["F5"] = "{F5}", ["F6"] = "{F6}", ["F7"] = "{F7}",
            ["F8"] = "{F8}", ["F9"] = "{F9}", ["F10"] = "{F10}", ["F11"] = "{F11}", ["F12"] = "{F12}"
        };
        foreach (var letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            allowed[letter.ToString()] = letter.ToString().ToLowerInvariant();
        if (!allowed.TryGetValue(key ?? "", out var sequence))
            throw new InvalidDataException("Niedozwolony klawisz specjalny.");
        var values = (modifiers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var prefix = "";
        if (values.Contains("Control", StringComparer.OrdinalIgnoreCase)) prefix += "^";
        if (values.Contains("Alt", StringComparer.OrdinalIgnoreCase)) prefix += "%";
        if (values.Contains("Shift", StringComparer.OrdinalIgnoreCase)) prefix += "+";
        return prefix + sequence;
    }

    private static object ReadClipboard()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var paths = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).Take(1).ToArray();
            if (paths.Length > 0)
            {
                var info = new FileInfo(paths[0]);
                if (info.Length > 512 * 1024)
                    return new { kind = "file", fileName = info.Name, bytes = info.Length,
                        tooLarge = true, maximumBytes = 512 * 1024 };
                return new { kind = "file", fileName = info.Name, bytes = info.Length,
                    fileBase64 = Convert.ToBase64String(File.ReadAllBytes(info.FullName)), tooLarge = false };
            }
        }
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (text.Length > 1024 * 1024) text = text[..(1024 * 1024)];
        return new { kind = "text", text };
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
    private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr deviceContext, int mode);
}

internal sealed record SessionRequest(string Type, string? Action, int? X, int? Y, int? Delta, int? MonitorIndex,
    int? MaxWidth, int? Quality, string? Text, string? Key, string? Modifiers,
    string? FileName, string? FileBase64);
internal sealed record SessionResponse(bool Ok, string Code, string? ImageBase64, int? Width, int? Height,
    JsonElement? Data = null, string? Error = null);

[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint Size;
    public uint Time;
}
