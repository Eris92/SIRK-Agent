from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROGRAM_PATH = ROOT / "src/SirkAgent.Session/Program.cs"


def replace_once(value: str, old: str, new: str, label: str) -> str:
    count = value.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one occurrence, found {count}")
    return value.replace(old, new, 1)


program = PROGRAM_PATH.read_text(encoding="utf-8-sig")
program = replace_once(
    program,
    '''        var encodeTimer = Stopwatch.StartNew();
        using var output = new MemoryStream();
        var encodeScale = forceFull || refinement ? fullScale : fullScale * deltaScalePercent / 100d;
        using var encodedBitmap = BuildEncodedBitmap(bitmap, bounds, dirtyRectangles, encodeScale, forceFull,
            out var fullFrame, out var patches);
''',
    '''        var encodeTimer = Stopwatch.StartNew();
        using var output = new MemoryStream();
        var encodeFullFrame = forceFull || DirtyRegionsRequireFullFrame(dirtyRectangles, bounds);
        var encodeScale = encodeFullFrame || refinement
            ? fullScale
            : fullScale * deltaScalePercent / 100d;
        using var encodedBitmap = BuildEncodedBitmap(bitmap, bounds, dirtyRectangles, encodeScale,
            encodeFullFrame, out var fullFrame, out var patches);
''',
    "full-frame scale selection",
)
program = replace_once(
    program,
    '''    private static Bitmap BuildEncodedBitmap(Bitmap source, Rectangle bounds, Rectangle[] dirtyRectangles,
        double scale, bool forceFull, out bool fullFrame, out DesktopPatch[] patches)
    {
''',
    '''    private static bool DirtyRegionsRequireFullFrame(Rectangle[] dirtyRectangles, Rectangle bounds)
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
''',
    "full-frame coverage helper",
)

if "var encodeScale = forceFull || refinement" in program:
    raise RuntimeError("Legacy low-resolution full-frame scale selection remains.")
if "encodeFullFrame" not in program or "DirtyRegionsRequireFullFrame" not in program:
    raise RuntimeError("Full-frame resolution fix was not applied.")

PROGRAM_PATH.write_text(program, encoding="utf-8", newline="\n")
print("Full-frame resolution fix applied.")
