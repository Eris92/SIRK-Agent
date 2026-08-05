$ErrorActionPreference = 'Stop'

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
