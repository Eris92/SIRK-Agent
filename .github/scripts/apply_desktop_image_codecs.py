from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SESSION = ROOT / "src/SirkAgent.Session/Program.cs"
SESSION_PROJECT = ROOT / "src/SirkAgent.Session/SirkAgent.Session.csproj"
SERVICE = ROOT / "src/SirkAgent.Service/DesktopStreamWorker.cs"
CONTRACT = ROOT / "tests/desktop-image-codec-contract.ps1"
WORKFLOW = ROOT / ".github/workflows/dotnet10-contract.yml"


def replace_once(value: str, old: str, new: str, label: str) -> str:
    count = value.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one occurrence, found {count}")
    return value.replace(old, new, 1)


project = SESSION_PROJECT.read_text(encoding="utf-8-sig")
project = replace_once(
    project,
    '    <PackageReference Include="System.Drawing.Common" Version="10.0.0" />\n',
    '    <PackageReference Include="System.Drawing.Common" Version="10.0.0" />\n'
    '    <PackageReference Include="SkiaSharp" Version="4.150.1" />\n',
    "SkiaSharp package",
)
SESSION_PROJECT.write_text(project, encoding="utf-8", newline="\n")

session = SESSION.read_text(encoding="utf-8-sig")
session = replace_once(
    session,
    "using System.Text.Json;\n",
    "using System.Text.Json;\nusing SkiaSharp;\n",
    "SkiaSharp namespace",
)
session = replace_once(
    session,
    '''    [STAThread]
    private static async Task<int> Main()
    {
        try
        {
            await RunAsync();
            return 0;
''',
    '''    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Any(value => string.Equals(value, "--codec-self-test",
                    StringComparison.OrdinalIgnoreCase)))
                return RunImageCodecSelfTest();
            await RunAsync();
            return 0;
''',
    "codec self-test entry point",
)
session = replace_once(
    session,
    '''                        "snapshot" => SnapshotPayload(request.MonitorIndex ?? -1,
                            request.MaxWidth ?? 1280, request.Quality ?? 40,
                            request.TargetFps ?? 60, request.DeltaScalePercent ?? 100,
                            request.ForceFull == true),
''',
    '''                        "snapshot" => SnapshotPayload(request.MonitorIndex ?? -1,
                            request.MaxWidth ?? 1280, request.Quality ?? 40,
                            request.TargetFps ?? 60, request.DeltaScalePercent ?? 100,
                            request.ImageEncoding ?? "webp", request.ForceFull == true),
''',
    "binary snapshot image encoding",
)
session = replace_once(
    session,
    '''                            "snapshot" => Snapshot(request.MonitorIndex ?? -1, request.MaxWidth ?? 1280,
                                request.Quality ?? 40, request.TargetFps ?? 60,
                                request.DeltaScalePercent ?? 100, request.ForceFull == true),
''',
    '''                            "snapshot" => Snapshot(request.MonitorIndex ?? -1, request.MaxWidth ?? 1280,
                                request.Quality ?? 40, request.TargetFps ?? 60,
                                request.DeltaScalePercent ?? 100,
                                request.ImageEncoding ?? "webp", request.ForceFull == true),
''',
    "legacy snapshot image encoding",
)
session = replace_once(
    session,
    '''    private static SessionResponse Snapshot(int monitorIndex, int maxWidth, int quality, int targetFps,
        int deltaScalePercent, bool requestedFull)
    {
        var payload = SnapshotPayload(monitorIndex, maxWidth, quality, targetFps, deltaScalePercent, requestedFull);
''',
    '''    private static SessionResponse Snapshot(int monitorIndex, int maxWidth, int quality, int targetFps,
        int deltaScalePercent, string imageEncoding, bool requestedFull)
    {
        var payload = SnapshotPayload(monitorIndex, maxWidth, quality, targetFps, deltaScalePercent,
            imageEncoding, requestedFull);
''',
    "snapshot signature",
)
session = replace_once(
    session,
    '''    private static SessionVideoPayload SnapshotPayload(int monitorIndex, int maxWidth, int quality,
        int targetFps, int deltaScalePercent, bool requestedFull)
    {
        lock (CaptureSync)
            return SnapshotPayloadLocked(monitorIndex, maxWidth, quality, targetFps, deltaScalePercent,
                requestedFull);
    }

    private static SessionVideoPayload SnapshotPayloadLocked(int monitorIndex, int maxWidth, int quality,
        int targetFps, int deltaScalePercent, bool requestedFull)
''',
    '''    private static SessionVideoPayload SnapshotPayload(int monitorIndex, int maxWidth, int quality,
        int targetFps, int deltaScalePercent, string imageEncoding, bool requestedFull)
    {
        lock (CaptureSync)
            return SnapshotPayloadLocked(monitorIndex, maxWidth, quality, targetFps, deltaScalePercent,
                imageEncoding, requestedFull);
    }

    private static SessionVideoPayload SnapshotPayloadLocked(int monitorIndex, int maxWidth, int quality,
        int targetFps, int deltaScalePercent, string imageEncoding, bool requestedFull)
''',
    "snapshot payload signatures",
)
session = replace_once(
    session,
    "        quality = Math.Clamp(quality, 25, 80);\n",
    "        quality = Math.Clamp(quality, 10, 100);\n"
    "        imageEncoding = NormalizeImageEncoding(imageEncoding);\n",
    "quality range and encoding normalization",
)
session = replace_once(
    session,
    '''        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        encodedBitmap.Save(output, JpegEncoder.Value, parameters);
        encodeTimer.Stop();
''',
    '''        var encodedAs = EncodeBitmap(encodedBitmap, output, imageEncoding, quality);
        encodeTimer.Stop();
''',
    "image encoder dispatch",
)
session = replace_once(
    session,
    '                encoding = "JPEG"\n',
    '                encoding = encodedAs\n',
    "dynamic encoding metadata",
)
insert_marker = '''    private static bool DirtyRegionsRequireFullFrame(Rectangle[] dirtyRectangles, Rectangle bounds)
'''
codec_helpers = r'''    private static string NormalizeImageEncoding(string? value) =>
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

'''
session = replace_once(session, insert_marker, codec_helpers + insert_marker, "codec helpers")
session = replace_once(
    session,
    '''internal sealed record SessionRequest(string Type, string? Action, int? X, int? Y, int? Delta, int? MonitorIndex,
    int? MaxWidth, int? Quality, int? TargetKbps, int? TargetFps, int? DeltaScalePercent,
    string? Text, string? Key, string? Modifiers,
''',
    '''internal sealed record SessionRequest(string Type, string? Action, int? X, int? Y, int? Delta, int? MonitorIndex,
    int? MaxWidth, int? Quality, int? TargetKbps, int? TargetFps, int? DeltaScalePercent,
    string? ImageEncoding, string? Text, string? Key, string? Modifiers,
''',
    "session request image encoding",
)
for required in ["SKEncodedImageFormat.Webp", 'encoding = encodedAs', "--codec-self-test",
                 "string? ImageEncoding"]:
    if required not in session:
        raise RuntimeError(f"Session codec marker missing: {required}")
SESSION.write_text(session, encoding="utf-8", newline="\n")

service = SERVICE.read_text(encoding="utf-8-sig")
service = replace_once(
    service,
    "    private int _profileQuality = 72;\n",
    "    private int _profileQuality = 85;\n"
    "    private string _imageEncoding = \"webp\";\n",
    "service image encoding state",
)
service = replace_once(
    service,
    '''                    deltaScalePercent = Volatile.Read(ref _deltaScalePercent),
                    forceFull
''',
    '''                    deltaScalePercent = Volatile.Read(ref _deltaScalePercent),
                    imageEncoding = Volatile.Read(ref _imageEncoding),
                    forceFull
''',
    "session capture image encoding",
)
service = replace_once(
    service,
    '''                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    encoding.StartsWith("H264", StringComparison.Ordinal) ? "video/h264" : "image/jpeg",
                    encoding, Bool(data, "keyFrame"), Bool(data, "cursorOnly"));
''',
    '''                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ContentTypeForEncoding(encoding),
                    encoding, Bool(data, "keyFrame"), Bool(data, "cursorOnly"));
''',
    "frame content type mapping",
)
service = replace_once(
    service,
    '''            var requestedDeltaScaleValue = Integer(input, "deltaScalePercent");
            var requestedDeltaScale = requestedDeltaScaleValue == 0 ? 100 :
                Math.Clamp(requestedDeltaScaleValue, 10, 100);
            var previousDirtyRegionMode = Volatile.Read(ref _dirtyRegionMode);
            var previousSessionId = Volatile.Read(ref _sessionId);
            if (requestedWidth != previousWidth || requestedFps != previousFps ||
                requestedDirtyRegionMode != previousDirtyRegionMode || requestedSessionId != previousSessionId)
''',
    '''            var requestedDeltaScaleValue = Integer(input, "deltaScalePercent");
            var requestedDeltaScale = requestedDeltaScaleValue == 0 ? 100 :
                Math.Clamp(requestedDeltaScaleValue, 10, 100);
            var requestedImageEncoding = NormalizeImageEncoding(Text(input, "imageEncoding"));
            var previousDirtyRegionMode = Volatile.Read(ref _dirtyRegionMode);
            var previousSessionId = Volatile.Read(ref _sessionId);
            var previousImageEncoding = Volatile.Read(ref _imageEncoding);
            if (requestedWidth != previousWidth || requestedFps != previousFps ||
                requestedDirtyRegionMode != previousDirtyRegionMode || requestedSessionId != previousSessionId ||
                !string.Equals(requestedImageEncoding, previousImageEncoding, StringComparison.Ordinal))
''',
    "stream profile image encoding",
)
service = replace_once(
    service,
    '''            Volatile.Write(ref _maxWidth, requestedWidth);
            Volatile.Write(ref _quality, Math.Clamp(Integer(input, "quality"), 25, 80));
            Volatile.Write(ref _profileQuality, Volatile.Read(ref _quality));
''',
    '''            Volatile.Write(ref _maxWidth, requestedWidth);
            var requestedQualityValue = Integer(input, "quality");
            var requestedQuality = requestedQualityValue == 0 ? 85 :
                Math.Clamp(requestedQualityValue, 10, 100);
            Volatile.Write(ref _quality, requestedQuality);
            Volatile.Write(ref _profileQuality, requestedQuality);
            Volatile.Write(ref _imageEncoding, requestedImageEncoding);
''',
    "quality range and image encoding write",
)
service = replace_once(
    service,
    '''                        frameMode = Volatile.Read(ref _dirtyRegionMode) != 0 ? "tiles" : "h264",
                        deltaScalePercent = Volatile.Read(ref _deltaScalePercent),
                        bitrateKbps
''',
    '''                        frameMode = Volatile.Read(ref _dirtyRegionMode) != 0 ? "tiles" : "h264",
                        imageEncoding = Volatile.Read(ref _imageEncoding),
                        deltaScalePercent = Volatile.Read(ref _deltaScalePercent),
                        bitrateKbps
''',
    "status image encoding",
)
service = replace_once(
    service,
    '''            else if (quality > 30) Volatile.Write(ref _quality, Math.Max(25, quality - 3));
''',
    '''            else if (!string.Equals(Volatile.Read(ref _imageEncoding), "png", StringComparison.Ordinal) &&
                     quality > 20) Volatile.Write(ref _quality, Math.Max(10, quality - 3));
''',
    "adaptive image quality minimum",
)
helper_marker = '''    private static string Number(JsonElement data, string name) =>
'''
service_helpers = '''    private static string NormalizeImageEncoding(string? value) =>
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

'''
service = replace_once(service, helper_marker, service_helpers + helper_marker, "service codec helpers")
for required in ['imageEncoding = Volatile.Read(ref _imageEncoding)', '"image/webp"',
                 '"image/png"', 'Math.Clamp(requestedQualityValue, 10, 100)']:
    if required not in service:
        raise RuntimeError(f"Service codec marker missing: {required}")
SERVICE.write_text(service, encoding="utf-8", newline="\n")

contract = r'''$ErrorActionPreference = 'Stop'

$sessionProject = Get-Content 'src/SirkAgent.Session/SirkAgent.Session.csproj' -Raw
$session = Get-Content 'src/SirkAgent.Session/Program.cs' -Raw
$service = Get-Content 'src/SirkAgent.Service/DesktopStreamWorker.cs' -Raw

if ($sessionProject -notmatch 'PackageReference Include="SkiaSharp"') {
    throw 'SkiaSharp WebP encoder dependency is missing.'
}
foreach ($marker in @('SKEncodedImageFormat.Webp', 'ImageFormat.Png', 'JpegEncoder.Value',
                      'string? ImageEncoding', '--codec-self-test')) {
    if ($session -notmatch [regex]::Escape($marker)) {
        throw "Session image codec marker is missing: $marker"
    }
}
foreach ($marker in @('image/webp', 'image/png', 'image/jpeg',
                      'imageEncoding = Volatile.Read(ref _imageEncoding)',
                      'Math.Clamp(requestedQualityValue, 10, 100)')) {
    if ($service -notmatch [regex]::Escape($marker)) {
        throw "Service image codec marker is missing: $marker"
    }
}
'''
CONTRACT.write_text(contract, encoding="utf-8", newline="\n")

workflow = WORKFLOW.read_text(encoding="utf-8-sig")
workflow = replace_once(
    workflow,
    '''          $process = Start-Process -FilePath (Join-Path $publish 'SirkAgent.Session.exe') `
            -PassThru -WindowStyle Hidden
''',
    '''          $codecTest = Start-Process -FilePath (Join-Path $publish 'SirkAgent.Session.exe') `
            -ArgumentList '--codec-self-test' -PassThru -Wait -WindowStyle Hidden
          if ($codecTest.ExitCode -ne 0) {
            throw "Session image codec self-test failed. ExitCode=$($codecTest.ExitCode)"
          }

          $process = Start-Process -FilePath (Join-Path $publish 'SirkAgent.Session.exe') `
            -PassThru -WindowStyle Hidden
''',
    "published codec self-test",
)
workflow = replace_once(
    workflow,
    '''          & pwsh -NoProfile -File tests/session-broker-readiness-contract.ps1
          if ($LASTEXITCODE -ne 0) { throw 'Session broker readiness contract failed.' }
''',
    '''          & pwsh -NoProfile -File tests/desktop-image-codec-contract.ps1
          if ($LASTEXITCODE -ne 0) { throw 'Desktop image codec contract failed.' }
          & pwsh -NoProfile -File tests/session-broker-readiness-contract.ps1
          if ($LASTEXITCODE -ne 0) { throw 'Session broker readiness contract failed.' }
''',
    "codec contract workflow invocation",
)
WORKFLOW.write_text(workflow, encoding="utf-8", newline="\n")

print("Desktop image codecs applied.")
