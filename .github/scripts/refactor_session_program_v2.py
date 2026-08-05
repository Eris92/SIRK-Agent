from __future__ import annotations
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / 'src/SirkAgent.Session/Program.cs'
text = path.read_text(encoding='utf-8-sig')

for line in (
    'using System.Collections.Specialized;\n',
    'using System.Windows.Forms;\n',
    'using System.Windows.Automation;\n',
):
    text = text.replace(line, '')
if 'using System.Drawing;\n' not in text:
    marker = 'using System.Diagnostics;\n'
    if marker not in text:
        raise RuntimeError('using insertion marker missing')
    text = text.replace(marker, marker + 'using System.Drawing;\n', 1)

text, count = re.subn(
    r'    private static SessionResponse Monitors\(\)\n    \{[\s\S]*?\n    \}\n\n    private static Rectangle CaptureBounds\(int monitorIndex\)\n    \{[\s\S]*?\n    \}\n',
    '''    private static SessionResponse Monitors()\n    {\n        var monitors = NativeDesktop.Monitors().Select(monitor => new\n        {\n            index = monitor.Index,\n            name = monitor.Name,\n            primary = monitor.Primary,\n            x = monitor.Bounds.X,\n            y = monitor.Bounds.Y,\n            width = monitor.Bounds.Width,\n            height = monitor.Bounds.Height\n        }).ToArray();\n        return new SessionResponse(true, "DESKTOP_MONITORS_OK", null, null, null,\n            JsonSerializer.SerializeToElement(new { sessionId = SessionId, monitors }, Json));\n    }\n\n    private static Rectangle CaptureBounds(int monitorIndex) =>\n        NativeDesktop.CaptureBounds(monitorIndex);\n''',
    text,
    count=1)
if count != 1:
    raise RuntimeError(f'monitor block: {count}')

text = text.replace(
    'Array.FindIndex(Screen.AllScreens, value => value.Primary)',
    'NativeDesktop.PrimaryMonitorIndex()')
for name in ('cursor', 'pointer', 'gpuCursor', 'previous'):
    text = text.replace(
        f'var {name} = Cursor.Position;',
        f'var {name} = NativeDesktop.GetCursorPosition();')
text = text.replace(
    'Cursor.Position = new Point(x, y);',
    'NativeDesktop.SetCursorPosition(new Point(x, y));')

old = '''            RunSta(() =>\n            {\n                if (text.Length == 0) Clipboard.Clear();\n                else Clipboard.SetText(text);\n                return true;\n            });'''
new = '''            if (text.Length == 0) NativeClipboard.Clear();\n            else NativeClipboard.SetText(text);'''
if text.count(old) != 1:
    raise RuntimeError('clipboard text block')
text = text.replace(old, new, 1)

old = '            var clipboard = RunSta(ReadClipboard);'
if text.count(old) != 1:
    raise RuntimeError('clipboard read block')
text = text.replace(old, '            var clipboard = NativeClipboard.Read();', 1)

old = '''            RunSta(() =>\n            {\n                Clipboard.SetFileDropList(new StringCollection { path });\n                return true;\n            });'''
if text.count(old) != 1:
    raise RuntimeError('clipboard file block')
text = text.replace(old, '            NativeClipboard.SetFileDrop(path);', 1)

old = '''        if (request.Action == "text")\n        {\n            RunSta(() => { SendKeys.SendWait(EscapeSendKeys(request.Text ?? "")); return true; });\n            return new SessionResponse(true, "DESKTOP_TEXT_OK", null, null, null);\n        }\n        if (request.Action == "key")\n        {\n            RunSta(() => { SendKeys.SendWait(KeySequence(request.Key, request.Modifiers)); return true; });\n            return new SessionResponse(true, "DESKTOP_KEY_OK", null, null, null);\n        }'''
new = '''        if (request.Action == "text")\n        {\n            NativeInput.SendText(request.Text ?? string.Empty);\n            return new SessionResponse(true, "DESKTOP_TEXT_OK", null, null, null);\n        }\n        if (request.Action == "key")\n        {\n            NativeInput.SendKey(request.Key, request.Modifiers);\n            return new SessionResponse(true, "DESKTOP_KEY_OK", null, null, null);\n        }'''
if text.count(old) != 1:
    raise RuntimeError('keyboard block')
text = text.replace(old, new, 1)

text, count = re.subn(
    r'\n    private static T RunSta<T>\(Func<T> action\)[\s\S]*?\n    private static object ReadClipboard\(\)[\s\S]*?\n    \}\n\n    private static SessionResponse Activity\(\)',
    '\n    private static SessionResponse Activity()', text, count=1)
if count != 1:
    raise RuntimeError(f'STA/clipboard helpers: {count}')

text = text.replace(
    'var clipboard = ClipboardMetadata();',
    'var clipboard = NativeClipboard.Metadata();')
text = text.replace(
    'var uiAutomation = UiAutomation(foreground);',
    'var uiAutomation = NativeDesktop.WindowMetadata(foreground);')
text, count = re.subn(
    r'\n    private static object\? UiAutomation\(IntPtr foreground\)[\s\S]*?\n    private static object ClipboardMetadata\(\)[\s\S]*?\n    \}\n\n    private static string\? Limit',
    '\n    private static string? Limit', text, count=1)
if count != 1:
    raise RuntimeError(f'WPF/clipboard metadata helpers: {count}')

patterns = (
    r'System\.Windows\.Forms', r'System\.Windows\.Automation',
    r'\bScreen\.AllScreens', r'\bSystemInformation\.VirtualScreen',
    r'\bCursor\.Position', r'(?<!Native)\bClipboard\.',
    r'\bSendKeys\.', r'\bAutomationElement\b', r'\bRunSta\('
)
for pattern in patterns:
    if re.search(pattern, text):
        raise RuntimeError(f'dependency remains: {pattern}')

path.write_text(text, encoding='utf-8', newline='\n')
print('Session Program.cs now uses native Win32 integration.')
