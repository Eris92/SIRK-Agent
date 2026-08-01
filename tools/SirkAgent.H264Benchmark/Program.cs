using System.Diagnostics;
using SharpMediaFoundationInterop.Input;
using SharpMediaFoundationInterop.Transforms.Colors;
using SharpMediaFoundationInterop.Transforms.H264;
using Vortice.MediaFoundation;
using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

MediaFactory.MFStartup().CheckError();

if (args.FirstOrDefault() == "reflect-video")
{
    foreach (var type in new[] { typeof(VideoProcessorContentDescription),
                 typeof(VideoProcessorInputViewDescription), typeof(VideoProcessorOutputViewDescription),
                 typeof(VideoProcessorStream), typeof(ID3D11VideoContext), typeof(ID3D11VideoProcessorEnumerator) })
    {
        Console.WriteLine("TYPE " + type.FullName);
        foreach (var member in type.GetMembers()) Console.WriteLine(member);
    }
    return;
}

if (args.FirstOrDefault() == "video-processor")
{
    const int inputWidth = 3440;
    const int inputHeight = 1440;
    const int outputWidth = 1920;
    const int outputHeight = 800;
    D3D11CreateDevice(null!, DriverType.Hardware,
        DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
        Array.Empty<FeatureLevel>(), out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
    using (device)
    using (context)
    using (var videoDevice = device.QueryInterface<ID3D11VideoDevice>())
    using (var videoContext = context.QueryInterface<ID3D11VideoContext>())
    using (var inputTexture = device.CreateTexture2D(new Texture2DDescription
           {
               Width = inputWidth, Height = inputHeight, MipLevels = 1, ArraySize = 1,
               Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
               Usage = ResourceUsage.Default, BindFlags = BindFlags.None
           }))
    using (var outputTexture = device.CreateTexture2D(new Texture2DDescription
           {
               Width = outputWidth, Height = outputHeight, MipLevels = 1, ArraySize = 1,
               Format = Format.NV12, SampleDescription = new(1, 0),
               Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
           }))
    {
        var description = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1), InputWidth = inputWidth, InputHeight = inputHeight,
            OutputFrameRate = new Rational(60, 1), OutputWidth = outputWidth, OutputHeight = outputHeight,
            Usage = VideoUsage.OptimalSpeed
        };
        using var enumerator = videoDevice.CreateVideoProcessorEnumerator(description);
        Console.WriteLine($"input_support={enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm)} output_support={enumerator.CheckVideoProcessorFormat(Format.NV12)}");
        using var processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        using var inputView = videoDevice.CreateVideoProcessorInputView(inputTexture, enumerator,
            new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
            });
        using var outputView = videoDevice.CreateVideoProcessorOutputView(outputTexture, enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
            });
        videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true,
            new Vortice.RawRect(0, 0, inputWidth, inputHeight));
        videoContext.VideoProcessorSetStreamDestRect(processor, 0, true,
            new Vortice.RawRect(0, 0, outputWidth, outputHeight));
        var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
        var timings = new List<double>();
        for (var index = 0; index < 600; index++)
        {
            var timer = Stopwatch.StartNew();
            videoContext.VideoProcessorBlt(processor, outputView, (uint)index, [stream]).CheckError();
            context.Flush();
            timer.Stop();
            timings.Add(timer.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        Console.WriteLine($"gpu_scale_convert_p50_ms={timings[300]:F3} gpu_scale_convert_p95_ms={timings[570]:F3}");
    }
    return;
}

if (args.FirstOrDefault() == "hardware-mft")
{
    const int width = 1280;
    const int height = 720;
    const int frameCount = 600;
    const int hardwareBitrate = 500_000;
    using var hardwareEncoder = new HardwareMftEncoder(width, height, 60, hardwareBitrate);
    var frame = new byte[width * height * 3 / 2];
    Array.Fill(frame, (byte)128);
    var timings = new List<double>();
    long hardwareBytes = 0;
    var hardwareOutputs = 0;
    var total = Stopwatch.StartNew();
    for (var index = 0; index < frameCount; index++)
    {
        var timer = Stopwatch.StartNew();
        hardwareBytes += hardwareEncoder.Encode(frame, index * 10_000_000L / 60, out var produced);
        timer.Stop();
        hardwareOutputs += produced;
        timings.Add(timer.Elapsed.TotalMilliseconds);
    }
    total.Stop();
    timings.Sort();
    Console.WriteLine($"hardware_mft_frames={frameCount} outputs={hardwareOutputs} throughput_fps={frameCount / total.Elapsed.TotalSeconds:F2} bitrate_mbps={hardwareBytes * 8 / (frameCount / 60d) / 1_000_000:F3}");
    Console.WriteLine($"hardware_mft_p50_ms={timings[(int)(timings.Count * .50)]:F3} hardware_mft_p95_ms={timings[(int)(timings.Count * .95)]:F3}");
    return;
}

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

internal sealed class HardwareMftEncoder : IDisposable
{
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly int _frameBytes;
    private readonly long _duration;
    private bool _needInput;

    public HardwareMftEncoder(int width, int height, int fps, int bitrate)
    {
        _frameBytes = width * height * 3 / 2;
        _duration = 10_000_000L / fps;
        var outputInfo = new RegisterTypeInfo
            { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        using var activations = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
            (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), null, outputInfo);
        var activation = activations.FirstOrDefault() ??
                         throw new NotSupportedException("Hardware H.264 MFT not found.");
        Console.WriteLine("selected_hardware_mft=" +
                          activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute));
        _transform = activation.ActivateObject<IMFTransform>();
        _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
        ConfigureCodec(_transform, bitrate, fps);

        using var outputType = _transform.GetOutputAvailableType(0, 0);
        Configure(outputType, width, height, fps);
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 66u).CheckError();
        _transform.SetOutputType(0, outputType, 0);

        using var inputType = InputType(_transform);
        Configure(inputType, width, height, fps);
        _transform.SetInputType(0, inputType, 0);

        _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        Console.WriteLine("hardware_mft_types_ready");
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Console.WriteLine("hardware_mft_stream_started");
        var ignoredBytes = 0;
        var ignoredOutputs = 0;
        WaitForInput(ref ignoredBytes, ref ignoredOutputs);
        Console.WriteLine("hardware_mft_input_ready");
    }

    private static IMFMediaType InputType(IMFTransform transform)
    {
        for (var index = 0; index < 32; index++)
        {
            IMFMediaType type;
            try { type = transform.GetInputAvailableType(0, index); }
            catch { break; }
            if (type.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoFormatGuids.NV12) return type;
            type.Dispose();
        }
        throw new NotSupportedException("Hardware H.264 MFT has no NV12 input.");
    }

    private static void ConfigureCodec(IMFTransform transform, int bitrate, int fps)
    {
        var interfaceId = typeof(ICodecApiNative).GUID;
        transform.QueryInterface(interfaceId, out var pointer).CheckError();
        try
        {
            var codec = (ICodecApiNative)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(pointer);
            Set(codec, "F7222374-2144-4815-B550-A37F8E12EE52", (uint)bitrate);
            Set(codec, "1C0608E9-370C-4710-8A58-CB6181C42423", 0u);
            Set(codec, "8D390AAC-DC5C-4200-B57F-814D04BABAB2", 0u);
            Set(codec, "95F31B26-95A4-41AA-9303-246A7FC6EEF1", (uint)fps);
            Set(codec, "0DB96574-B6A4-4C8B-8106-3773DE0310CD", (uint)(bitrate / 4));
            Set(codec, "B28A6E64-3FF9-446A-8A4B-0D7A53413236", 1u);
            Set(codec, "9C27891A-ED7A-40E1-88E8-B22727A024EE", 1u);
        }
        finally { System.Runtime.InteropServices.Marshal.Release(pointer); }
    }

    private static void Set(ICodecApiNative codec, string keyValue, object value)
    {
        var key = new Guid(keyValue);
        var result = codec.SetValue(ref key, ref value);
        if (result < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
    }

    private static void Configure(IMFMediaType type, int width, int height, int fps)
    {
        type.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height)).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(fps, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
    }

    public int Encode(byte[] nv12, long timestamp, out int outputs)
    {
        outputs = 0;
        var bytes = 0;
        DrainAvailable(ref bytes, ref outputs);
        WaitForInput(ref bytes, ref outputs);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(_frameBytes);
        buffer.Lock(out var pointer, out _, out _);
        try { System.Runtime.InteropServices.Marshal.Copy(nv12, 0, pointer, _frameBytes); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = _frameBytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = timestamp;
        sample.SampleDuration = _duration;
        _transform.ProcessInput(0, sample, 0);
        _needInput = false;

        while (!_needInput)
        {
            using var mediaEvent = _events.GetEvent(0);
            ProcessEvent(mediaEvent, ref bytes, ref outputs);
        }
        return bytes;
    }

    private void DrainAvailable(ref int bytes, ref int outputs)
    {
        while (true)
        {
            try
            {
                using var mediaEvent = _events.GetEvent(1);
                ProcessEvent(mediaEvent, ref bytes, ref outputs);
            }
            catch { return; }
        }
    }

    private void WaitForInput(ref int bytes, ref int outputs)
    {
        while (!_needInput)
        {
            using var mediaEvent = _events.GetEvent(0);
            ProcessEvent(mediaEvent, ref bytes, ref outputs);
        }
    }

    private void ProcessEvent(IMFMediaEvent mediaEvent, ref int bytes, ref int outputs)
    {
        if (mediaEvent.EventType == MediaEventTypes.TransformNeedInput)
        {
            _needInput = true;
            return;
        }
        if (mediaEvent.EventType != MediaEventTypes.TransformHaveOutput) return;
        var output = new OutputDataBuffer { StreamID = 0 };
        var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
        output.Events?.Dispose();
        if (result.Failure || output.Sample is null) return;
        using (output.Sample)
        using (var contiguous = output.Sample.ConvertToContiguousBuffer())
            bytes += contiguous.CurrentLength;
        outputs++;
    }

    public void Dispose()
    {
        _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        _events.Dispose();
        _transform.Dispose();
    }
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApiNative
{
    [System.Runtime.InteropServices.PreserveSig] int IsSupported(ref Guid api);
    [System.Runtime.InteropServices.PreserveSig] int IsModifiable(ref Guid api);
    [System.Runtime.InteropServices.PreserveSig] int GetParameterRange(ref Guid api, out object min, out object max, out object step);
    [System.Runtime.InteropServices.PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint count);
    [System.Runtime.InteropServices.PreserveSig] int GetDefaultValue(ref Guid api, out object value);
    [System.Runtime.InteropServices.PreserveSig] int GetValue(ref Guid api, out object value);
    [System.Runtime.InteropServices.PreserveSig] int SetValue(ref Guid api,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Struct)] ref object value);
    [System.Runtime.InteropServices.PreserveSig] int RegisterForEvent(ref Guid api, IntPtr userData);
    [System.Runtime.InteropServices.PreserveSig] int UnregisterForEvent(ref Guid api);
}
