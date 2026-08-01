using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Win32;
using Vortice.MediaFoundation;
using Vortice.Direct3D11;
using MfColorConverter = SharpMediaFoundationInterop.Transforms.Colors.ColorConverter;

namespace SirkAgent.Session;

internal sealed class SessionH264Encoder : IDisposable
{
    private readonly MfColorConverter _converter;
    private readonly DirectHardwareH264Encoder _encoder;
    private readonly byte[] _rgb;
    private byte[] _nv12;
    private long _timestamp;
    private readonly int _bitrate;
    public int Width { get; }
    public int Height { get; }
    public bool LastWasKeyFrame { get; private set; }
    public bool HasProducedFrame { get; private set; }
    public int InputFrames { get; private set; }
    public int OutputFrames => _encoder.OutputFrames;

    public SessionH264Encoder(int width, int height, int bitrate)
    {
        Width = width; Height = height; _bitrate = bitrate;
        _encoder = new DirectHardwareH264Encoder(width, height, 60, bitrate);
        _converter = new MfColorConverter(PInvoke.MFVideoFormat_RGB24, PInvoke.MFVideoFormat_NV12,
            (uint)width, (uint)height);
        _converter.Initialize();
        _rgb = new byte[checked(width * height * 3)];
        _nv12 = new byte[_converter.OutputSize];
    }

    public bool Matches(int width, int height, int bitrate) =>
        Width == width && Height == height && _bitrate == bitrate;

    public byte[] Encode(Bitmap bitmap)
    {
        CopyRgb(bitmap);
        if (!_converter.ProcessInput(_rgb, _timestamp)) throw new InvalidOperationException("RGB24 converter rejected input.");
        if (!_converter.ProcessOutput(ref _nv12, out _)) throw new InvalidOperationException("RGB24 converter produced no NV12 output.");
        var bytes = _encoder.Encode(_nv12, _timestamp);
        InputFrames++;
        _timestamp += 10_000_000L / 60;
        HasProducedFrame |= bytes.Length > 0;
        LastWasKeyFrame = ContainsIdr(bytes);
        return bytes;
    }

    public bool RequestKeyFrame() => _encoder.RequestKeyFrame();

    private unsafe void CopyRgb(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            fixed (byte* target = _rgb)
                for (var y = 0; y < Height; y++)
                    Buffer.MemoryCopy((byte*)data.Scan0 + (Height - 1 - y) * data.Stride,
                        target + y * Width * 3, Width * 3, Width * 3);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static bool ContainsIdr(byte[] bytes)
    {
        for (var i = 0; i + 4 < bytes.Length; i++)
            if (bytes[i] == 0 && bytes[i + 1] == 0 &&
                ((bytes[i + 2] == 1 && (bytes[i + 3] & 31) == 5) ||
                 (bytes[i + 2] == 0 && bytes[i + 3] == 1 && (bytes[i + 4] & 31) == 5))) return true;
        return false;
    }

    public void Dispose() { _encoder.Dispose(); _converter.Dispose(); }
}

internal sealed class DirectHardwareH264Encoder : IDisposable
{
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly int _frameBytes;
    private readonly long _duration;
    private bool _needInput;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private bool _mediaFoundationStarted;
    private bool _disposed;
    public int OutputFrames { get; private set; }

    public DirectHardwareH264Encoder(int width, int height, int fps, int bitrate, ID3D11Device? device = null)
    {
        MediaFactory.MFStartup().CheckError();
        _mediaFoundationStarted = true;
        try
        {
            _frameBytes = width * height * 3 / 2;
            _duration = 10_000_000L / fps;
            var outputInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
            using var activations = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
                (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter), null, outputInfo);
            var activation = activations.FirstOrDefault() ?? throw new NotSupportedException("Brak sprzętowego kodera H.264 Media Foundation.");
            _transform = activation.ActivateObject<IMFTransform>();
            _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            if (device is not null)
            {
                _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
                _deviceManager.ResetDevice(device).CheckError();
                _transform.ProcessMessage(TMessageType.MessageSetD3DManager,
                    unchecked((UIntPtr)_deviceManager.NativePointer.ToInt64()));
            }
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
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            using var sink = new MemoryStream();
            WaitForInput(sink);
        }
        catch
        {
            MediaFactory.MFShutdown();
            _mediaFoundationStarted = false;
            throw;
        }
    }

    public bool RequestKeyFrame()
    {
        object? codecObject = null;
        try
        {
            _transform.QueryInterface(typeof(ICodecApiNative).GUID, out var pointer).CheckError();
            try
            {
                codecObject = Marshal.GetObjectForIUnknown(pointer);
                var codec = (ICodecApiNative)codecObject;
                Set(codec, "398C1B98-8353-475A-9EF2-8F265D260345", 1u);
                return true;
            }
            finally
            {
                if (codecObject is not null && Marshal.IsComObject(codecObject))
                    Marshal.FinalReleaseComObject(codecObject);
                Marshal.Release(pointer);
            }
        }
        catch { return false; }
    }

    public byte[] Encode(byte[] nv12, long timestamp)
    {
        using var output = new MemoryStream();
        DrainAvailable(output);
        WaitForInput(output);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(_frameBytes);
        buffer.Lock(out var pointer, out _, out _);
        try { Marshal.Copy(nv12, 0, pointer, _frameBytes); } finally { buffer.Unlock(); }
        buffer.CurrentLength = _frameBytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer); sample.SampleTime = timestamp; sample.SampleDuration = _duration;
        _transform.ProcessInput(0, sample, 0); _needInput = false;
        while (!_needInput) { using var mediaEvent = _events.GetEvent(0); ProcessEvent(mediaEvent, output); }
        return output.ToArray();
    }

    public byte[] Encode(ID3D11Texture2D texture, long timestamp)
    {
        using var output = new MemoryStream();
        DrainAvailable(output); WaitForInput(output);
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID,
            texture, 0, false);
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer); sample.SampleTime = timestamp; sample.SampleDuration = _duration;
        _transform.ProcessInput(0, sample, 0); _needInput = false;
        while (!_needInput) { using var mediaEvent = _events.GetEvent(0); ProcessEvent(mediaEvent, output); }
        return output.ToArray();
    }

    private void DrainAvailable(Stream output)
    {
        while (true) { try { using var e = _events.GetEvent(1); ProcessEvent(e, output); } catch { return; } }
    }
    private void WaitForInput(Stream output)
    {
        while (!_needInput) { using var e = _events.GetEvent(0); ProcessEvent(e, output); }
    }
    private void ProcessEvent(IMFMediaEvent mediaEvent, Stream destination)
    {
        if (mediaEvent.EventType == MediaEventTypes.TransformNeedInput) { _needInput = true; return; }
        if (mediaEvent.EventType != MediaEventTypes.TransformHaveOutput) return;
        var output = new OutputDataBuffer { StreamID = 0 };
        var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
        output.Events?.Dispose();
        if (result.Failure || output.Sample is null) return;
        OutputFrames++;
        using (output.Sample)
        using (var contiguous = output.Sample.ConvertToContiguousBuffer())
        {
            contiguous.Lock(out var pointer, out _, out var length);
            try { var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); destination.Write(bytes); }
            finally { contiguous.Unlock(); }
        }
    }
    private static IMFMediaType InputType(IMFTransform transform)
    {
        for (var index = 0; index < 32; index++)
        {
            IMFMediaType type; try { type = transform.GetInputAvailableType(0, index); } catch { break; }
            if (type.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoFormatGuids.NV12) return type;
            type.Dispose();
        }
        throw new NotSupportedException("Sprzętowy encoder nie obsługuje NV12.");
    }
    private static void Configure(IMFMediaType type, int width, int height, int fps)
    {
        type.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height)).CheckError();
        type.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(fps, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1)).CheckError();
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
    }
    private static void ConfigureCodec(IMFTransform transform, int bitrate, int fps)
    {
        transform.QueryInterface(typeof(ICodecApiNative).GUID, out var pointer).CheckError();
        object? codecObject = null;
        try
        {
            codecObject = Marshal.GetObjectForIUnknown(pointer);
            var codec = (ICodecApiNative)codecObject;
            Set(codec, "F7222374-2144-4815-B550-A37F8E12EE52", (uint)bitrate);
            Set(codec, "1C0608E9-370C-4710-8A58-CB6181C42423", 0u);
            Set(codec, "8D390AAC-DC5C-4200-B57F-814D04BABAB2", 0u);
            Set(codec, "95F31B26-95A4-41AA-9303-246A7FC6EEF1", (uint)fps);
            Set(codec, "B28A6E64-3FF9-446A-8A4B-0D7A53413236", 1u);
            Set(codec, "9C27891A-ED7A-40E1-88E8-B22727A024EE", 1u);
        }
        finally
        {
            if (codecObject is not null && Marshal.IsComObject(codecObject))
                Marshal.FinalReleaseComObject(codecObject);
            Marshal.Release(pointer);
        }
    }
    private static void Set(ICodecApiNative codec, string keyValue, object value)
    {
        var key = new Guid(keyValue); var result = codec.SetValue(ref key, ref value);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _transform?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try { _events?.Dispose(); } catch { }
        try { _transform?.Dispose(); } catch { }
        try { _deviceManager?.Dispose(); } catch { }
        if (_mediaFoundationStarted)
        {
            try { MediaFactory.MFShutdown(); } catch { }
            _mediaFoundationStarted = false;
        }
    }
}

[ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApiNative
{
    [PreserveSig] int IsSupported(ref Guid api); [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, out object min, out object max, out object step);
    [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(ref Guid api, out object value); [PreserveSig] int GetValue(ref Guid api, out object value);
    [PreserveSig] int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    [PreserveSig] int RegisterForEvent(ref Guid api, IntPtr userData); [PreserveSig] int UnregisterForEvent(ref Guid api);
}
