using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace SirkAgent.Session;

internal sealed record NativeMonitor(
    int Index,
    string Name,
    bool Primary,
    Rectangle Bounds);

internal static class NativeDesktop
{
    private const int MonitorInfoPrimary = 0x00000001;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    internal static NativeMonitor[] Monitors()
    {
        var result = new List<NativeMonitor>();
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfo failed.");
            result.Add(new NativeMonitor(
                result.Count,
                string.IsNullOrWhiteSpace(info.DeviceName) ? $"DISPLAY{result.Count + 1}" : info.DeviceName,
                (info.Flags & MonitorInfoPrimary) != 0,
                info.Monitor.ToRectangle()));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
        if (result.Count == 0)
            throw new InvalidOperationException("No active display monitor was found.");
        return result.ToArray();
    }

    internal static Rectangle CaptureBounds(int monitorIndex)
    {
        if (monitorIndex < 0)
        {
            return new Rectangle(
                GetSystemMetrics(SmXVirtualScreen),
                GetSystemMetrics(SmYVirtualScreen),
                GetSystemMetrics(SmCxVirtualScreen),
                GetSystemMetrics(SmCyVirtualScreen));
        }
        var monitors = Monitors();
        if (monitorIndex >= monitors.Length)
            throw new InvalidDataException("Wybrany monitor nie istnieje.");
        return monitors[monitorIndex].Bounds;
    }

    internal static int PrimaryMonitorIndex()
    {
        var monitors = Monitors();
        var index = Array.FindIndex(monitors, monitor => monitor.Primary);
        return Math.Max(0, index);
    }

    internal static Point GetCursorPosition()
    {
        if (!GetCursorPos(out var point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos failed.");
        return new Point(point.X, point.Y);
    }

    internal static void SetCursorPosition(Point point)
    {
        if (!SetCursorPos(point.X, point.Y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
    }

    internal static object WindowMetadata(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return new { available = false, provider = "Win32", error = "NoForegroundWindow" };
        try
        {
            var titleLength = Math.Clamp(GetWindowTextLength(window), 0, 4096);
            var title = new StringBuilder(titleLength + 1);
            _ = GetWindowText(window, title, title.Capacity);
            var className = new StringBuilder(512);
            _ = GetClassName(window, className, className.Capacity);
            if (!GetWindowRect(window, out var bounds))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
            return new
            {
                available = true,
                provider = "Win32",
                name = Limit(title.ToString(), 512),
                automationId = (string?)null,
                controlType = "ControlType.Window",
                className = Limit(className.ToString(), 256),
                frameworkId = (string?)null,
                bounds = new
                {
                    x = bounds.Left,
                    y = bounds.Top,
                    width = Math.Max(0, bounds.Right - bounds.Left),
                    height = Math.Max(0, bounds.Bottom - bounds.Top)
                }
            };
        }
        catch (Exception error)
        {
            return new { available = false, provider = "Win32", error = error.GetType().Name };
        }
    }

    private static string? Limit(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRectangle,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public Rectangle ToRectangle() => new(
            Left,
            Top,
            Math.Max(0, Right - Left),
            Math.Max(0, Bottom - Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }
}

internal static class NativeInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkShift = 0x10;

    internal static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(Keyboard(0, character, KeyEventUnicode));
            inputs.Add(Keyboard(0, character, KeyEventUnicode | KeyEventKeyUp));
        }
        Send(inputs);
    }

    internal static void SendKey(string? key, string? modifiers)
    {
        var virtualKey = ResolveVirtualKey(key);
        var modifierKeys = ResolveModifiers(modifiers);
        var inputs = new List<Input>(modifierKeys.Count * 2 + 2);
        inputs.AddRange(modifierKeys.Select(value => Keyboard(value, 0, 0)));
        inputs.Add(Keyboard(virtualKey, 0, 0));
        inputs.Add(Keyboard(virtualKey, 0, KeyEventKeyUp));
        for (var index = modifierKeys.Count - 1; index >= 0; index--)
            inputs.Add(Keyboard(modifierKeys[index], 0, KeyEventKeyUp));
        Send(inputs);
    }

    private static List<ushort> ResolveModifiers(string? modifiers)
    {
        var values = (modifiers ?? string.Empty).Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<ushort>(3);
        if (values.Contains("Control", StringComparer.OrdinalIgnoreCase)) result.Add(VkControl);
        if (values.Contains("Alt", StringComparer.OrdinalIgnoreCase)) result.Add(VkMenu);
        if (values.Contains("Shift", StringComparer.OrdinalIgnoreCase)) result.Add(VkShift);
        return result;
    }

    private static ushort ResolveVirtualKey(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && key.Length == 1)
        {
            var value = char.ToUpperInvariant(key[0]);
            if (value is >= 'A' and <= 'Z') return value;
        }
        return key?.Trim() switch
        {
            "Enter" => 0x0D,
            "Tab" => 0x09,
            "Escape" => 0x1B,
            "Backspace" => 0x08,
            "Delete" => 0x2E,
            "Up" => 0x26,
            "Down" => 0x28,
            "Left" => 0x25,
            "Right" => 0x27,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => throw new InvalidDataException("Niedozwolony klawisz specjalny.")
        };
    }

    private static Input Keyboard(ushort virtualKey, char scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                ScanCode = scanCode,
                Flags = flags,
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            }
        }
    };

    private static void Send(IReadOnlyCollection<Input> values)
    {
        if (values.Count == 0) return;
        var inputs = values.ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"SendInput sent {sent} of {inputs.Length} events.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}

internal static class NativeClipboard
{
    private const uint CfText = 1;
    private const uint CfBitmap = 2;
    private const uint CfMetafilePicture = 3;
    private const uint CfSylk = 4;
    private const uint CfDif = 5;
    private const uint CfTiff = 6;
    private const uint CfOemText = 7;
    private const uint CfDib = 8;
    private const uint CfPalette = 9;
    private const uint CfPenData = 10;
    private const uint CfRiff = 11;
    private const uint CfWave = 12;
    private const uint CfUnicodeText = 13;
    private const uint CfEnhancedMetafile = 14;
    private const uint CfHDrop = 15;
    private const uint CfLocale = 16;
    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;

    internal static void Clear()
    {
        WithOpenClipboard(() =>
        {
            if (!EmptyClipboard())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");
        });
    }

    internal static void SetText(string text)
    {
        WithOpenClipboard(() =>
        {
            if (!EmptyClipboard())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");
            SetGlobalClipboardData(CfUnicodeText, Encoding.Unicode.GetBytes(text + '\0'));
        });
    }

    internal static void SetFileDrop(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var files = Encoding.Unicode.GetBytes(fullPath + "\0\0");
        var headerSize = Marshal.SizeOf<DropFiles>();
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)(headerSize + files.Length));
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed.");
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed.");
            try
            {
                Marshal.StructureToPtr(new DropFiles
                {
                    Offset = (uint)headerSize,
                    Point = default,
                    NonClient = 0,
                    Wide = 1
                }, pointer, false);
                Marshal.Copy(files, 0, IntPtr.Add(pointer, headerSize), files.Length);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
            WithOpenClipboard(() =>
            {
                if (!EmptyClipboard())
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EmptyClipboard failed.");
                if (SetClipboardData(CfHDrop, handle) == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetClipboardData failed.");
                handle = IntPtr.Zero;
            });
        }
        finally
        {
            if (handle != IntPtr.Zero) _ = GlobalFree(handle);
        }
    }

    internal static object Read()
    {
        return WithOpenClipboard(() =>
        {
            if (IsClipboardFormatAvailable(CfHDrop))
            {
                var handle = GetClipboardData(CfHDrop);
                var count = handle == IntPtr.Zero ? 0 : DragQueryFile(handle, uint.MaxValue, null, 0);
                if (count > 0)
                {
                    var length = DragQueryFile(handle, 0, null, 0);
                    var path = new StringBuilder((int)length + 1);
                    _ = DragQueryFile(handle, 0, path, (uint)path.Capacity);
                    if (File.Exists(path.ToString()))
                    {
                        var info = new FileInfo(path.ToString());
                        if (info.Length > 512 * 1024)
                            return (object)new
                            {
                                kind = "file",
                                fileName = info.Name,
                                bytes = info.Length,
                                tooLarge = true,
                                maximumBytes = 512 * 1024
                            };
                        return new
                        {
                            kind = "file",
                            fileName = info.Name,
                            bytes = info.Length,
                            fileBase64 = Convert.ToBase64String(File.ReadAllBytes(info.FullName)),
                            tooLarge = false
                        };
                    }
                }
            }
            var text = ReadUnicodeText();
            if (text.Length > 1024 * 1024) text = text[..(1024 * 1024)];
            return (object)new { kind = "text", text };
        });
    }

    internal static object Metadata()
    {
        try
        {
            return WithOpenClipboard(() =>
            {
                var formats = EnumerateFormats().Take(32).ToArray();
                var fileCount = 0u;
                if (IsClipboardFormatAvailable(CfHDrop))
                {
                    var handle = GetClipboardData(CfHDrop);
                    if (handle != IntPtr.Zero)
                        fileCount = DragQueryFile(handle, uint.MaxValue, null, 0);
                }
                var textLength = ReadUnicodeText().Length;
                return (object)new
                {
                    available = true,
                    provider = "Win32",
                    formats,
                    fileCount,
                    textLength
                };
            });
        }
        catch (Exception error)
        {
            return new { available = false, provider = "Win32", error = error.GetType().Name };
        }
    }

    private static string ReadUnicodeText()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText)) return string.Empty;
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero) return string.Empty;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return string.Empty;
        try
        {
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    private static IEnumerable<string> EnumerateFormats()
    {
        var format = 0u;
        while ((format = EnumClipboardFormats(format)) != 0)
            yield return FormatName(format);
    }

    private static string FormatName(uint format)
    {
        var predefined = format switch
        {
            CfText => "CF_TEXT",
            CfBitmap => "CF_BITMAP",
            CfMetafilePicture => "CF_METAFILEPICT",
            CfSylk => "CF_SYLK",
            CfDif => "CF_DIF",
            CfTiff => "CF_TIFF",
            CfOemText => "CF_OEMTEXT",
            CfDib => "CF_DIB",
            CfPalette => "CF_PALETTE",
            CfPenData => "CF_PENDATA",
            CfRiff => "CF_RIFF",
            CfWave => "CF_WAVE",
            CfUnicodeText => "CF_UNICODETEXT",
            CfEnhancedMetafile => "CF_ENHMETAFILE",
            CfHDrop => "CF_HDROP",
            CfLocale => "CF_LOCALE",
            CfDibV5 => "CF_DIBV5",
            _ => null
        };
        if (predefined is not null) return predefined;
        var name = new StringBuilder(256);
        var length = GetClipboardFormatName(format, name, name.Capacity);
        return length > 0 ? name.ToString() : $"FORMAT_{format}";
    }

    private static void SetGlobalClipboardData(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalAlloc failed.");
        try
        {
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GlobalLock failed.");
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
            if (SetClipboardData(format, handle) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetClipboardData failed.");
            handle = IntPtr.Zero;
        }
        finally
        {
            if (handle != IntPtr.Zero) _ = GlobalFree(handle);
        }
    }

    private static void WithOpenClipboard(Action action)
    {
        OpenClipboardWithRetry();
        try { action(); }
        finally { _ = CloseClipboard(); }
    }

    private static T WithOpenClipboard<T>(Func<T> action)
    {
        OpenClipboardWithRetry();
        try { return action(); }
        finally { _ = CloseClipboard(); }
    }

    private static void OpenClipboardWithRetry()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return;
            Thread.Sleep(25 + attempt * 5);
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenClipboard failed.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClipboardFormatName(uint format, StringBuilder name, int maximum);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint file, StringBuilder? path, uint maximum);

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint Offset;
        public NativePoint Point;
        public int NonClient;
        public int Wide;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
