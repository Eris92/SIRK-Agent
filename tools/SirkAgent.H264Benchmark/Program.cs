using System.Diagnostics;
using SharpMediaFoundationInterop.Input;
using SharpMediaFoundationInterop.Transforms.Colors;
using SharpMediaFoundationInterop.Transforms.H264;
using Vortice.MediaFoundation;

MediaFactory.MFStartup().CheckError();
var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
using (var transforms = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
    (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), input, output))
{
    foreach (var transform in transforms)
        Console.WriteLine("hardware_mft=" + transform.GetString(TransformAttributeKeys.MftFriendlyNameAttribute));
}

var seconds = args.Length > 0 ? Math.Clamp(int.Parse(args[0]), 2, 60) : 10;
const uint fps = 60;
const uint bitrate = 1_000_000;

using var capture = new ScreenCapture();
capture.Initialize();
using var encoder = new H264Encoder(capture.Width, capture.Height, fps, 1, bitrate);
encoder.Initialize();
using var converter = new ColorConverter(capture.OutputFormat, encoder.InputFormat,
    capture.Width, capture.Height);
converter.Initialize();

var rgba = new byte[capture.OutputSize];
var nv12 = new byte[converter.OutputSize];
var nalu = new byte[encoder.OutputSize];
var convertMs = new List<double>();
var encodeMs = new List<double>();
long bytes = 0;
var frames = 0;
var outputs = 0;
var clock = Stopwatch.StartNew();
var interval = Stopwatch.Frequency / (double)fps;
var nextTick = Stopwatch.GetTimestamp();

while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
{
    var now = Stopwatch.GetTimestamp();
    if (now < nextTick)
    {
        Thread.SpinWait(64);
        continue;
    }
    nextTick += (long)interval;
    if (!capture.ReadSample(rgba, out var timestamp)) continue;

    var step = Stopwatch.StartNew();
    if (!converter.ProcessInput(rgba, timestamp) || !converter.ProcessOutput(ref nv12, out _)) continue;
    step.Stop();
    convertMs.Add(step.Elapsed.TotalMilliseconds);

    step.Restart();
    if (!encoder.ProcessInput(nv12, timestamp)) continue;
    frames++;
    while (encoder.ProcessOutput(ref nalu, out var length))
    {
        bytes += length;
        outputs++;
    }
    step.Stop();
    encodeMs.Add(step.Elapsed.TotalMilliseconds);
}

static double Percentile(List<double> values, double percentile)
{
    if (values.Count == 0) return 0;
    values.Sort();
    return values[(int)Math.Min(values.Count - 1, Math.Ceiling(values.Count * percentile) - 1)];
}

Console.WriteLine($"capture={capture.Width}x{capture.Height} requested_fps={fps} requested_bitrate={bitrate}");
Console.WriteLine($"frames={frames} outputs={outputs} elapsed={clock.Elapsed.TotalSeconds:F2}s fps={frames / clock.Elapsed.TotalSeconds:F2} bitrate_mbps={bytes * 8 / clock.Elapsed.TotalSeconds / 1_000_000:F3}");
Console.WriteLine($"convert_p50_ms={Percentile(convertMs, .50):F2} convert_p95_ms={Percentile(convertMs, .95):F2}");
Console.WriteLine($"encode_p50_ms={Percentile(encodeMs, .50):F2} encode_p95_ms={Percentile(encodeMs, .95):F2}");
