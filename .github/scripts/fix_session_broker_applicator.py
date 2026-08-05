from pathlib import Path

path = Path(__file__).with_name("apply_session_broker_readiness.py")
text = path.read_text(encoding="utf-8")
old = '''pipe, count = re.subn(
    r"    internal static bool EnsureAvailable\\(int sessionId\\)\\n    \\{[\\s\\S]*?\\n    \\}\\n\\n    private static int\\? ResolveActiveSession\\(\\)",
    new_ensure + "\\n    private static int? ResolveActiveSession()",
    pipe,
    count=1,
)'''
new = '''pipe, count = re.subn(
    r"    internal static bool EnsureAvailable\\(int sessionId\\)\\n    \\{[\\s\\S]*?\\n    \\}\\n\\n    private static int\\? ResolveActiveSession\\(\\)",
    lambda _: new_ensure + "\\n    private static int? ResolveActiveSession()",
    pipe,
    count=1,
)'''
if text.count(old) != 1:
    raise RuntimeError(f"Applicator replacement target count: {text.count(old)}")
path.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")
print("Broker applicator now uses a literal replacement callback.")
