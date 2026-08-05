from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / 'src/SirkAgent.Session/NativeDesktop.cs'
text = path.read_text(encoding='utf-8-sig')
replacements = {
    'Keyboard(value, 0, 0)': "Keyboard(value, '\\0', 0)",
    'Keyboard(virtualKey, 0, 0)': "Keyboard(virtualKey, '\\0', 0)",
    'Keyboard(virtualKey, 0, KeyEventKeyUp)': "Keyboard(virtualKey, '\\0', KeyEventKeyUp)",
    'Keyboard(modifierKeys[index], 0, KeyEventKeyUp)': "Keyboard(modifierKeys[index], '\\0', KeyEventKeyUp)",
}
for old, new in replacements.items():
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{old}: expected one occurrence, found {count}')
    text = text.replace(old, new, 1)
path.write_text(text, encoding='utf-8', newline='\n')
print('Native keyboard scan codes use explicit null characters.')
