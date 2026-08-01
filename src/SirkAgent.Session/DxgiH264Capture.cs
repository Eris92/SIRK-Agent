using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace SirkAgent.Session;

internal sealed class DxgiH264Capture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _outputTexture;
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private readonly DirectHardwareH264Encoder _encoder;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _hasEncodedInput;

    public int Width { get; }
    public int Height { get; }
    public int TargetKbps { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public DxgiH264Capture(int outputIndex, int maxWidth, int targetKbps)
    {
        TargetKbps = targetKbps;
        D3D11CreateDevice(null!, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            Array.Empty<FeatureLevel>(), out _device, out _context).CheckError();
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs((uint)outputIndex, out var selectedOutput).CheckError();
        using var output = selectedOutput;
        var desktop = output.Description.DesktopCoordinates;
        SourceWidth = desktop.Right - desktop.Left;
        SourceHeight = desktop.Bottom - desktop.Top;
        var scale = Math.Min(1d, Math.Min((double)maxWidth / SourceWidth, 1080d / SourceHeight));
        Width = Math.Max(16, (int)Math.Round(SourceWidth * scale) & ~15);
        Height = Math.Max(16, (int)Math.Round(SourceHeight * scale) & ~15);
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1), InputWidth = (uint)SourceWidth, InputHeight = (uint)SourceHeight,
            OutputFrameRate = new Rational(60, 1), OutputWidth = (uint)Width, OutputHeight = (uint)Height,
            Usage = VideoUsage.OptimalSpeed
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        if (!_enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm).HasFlag(VideoProcessorFormatSupport.Input) ||
            !_enumerator.CheckVideoProcessorFormat(Format.NV12).HasFlag(VideoProcessorFormatSupport.Output))
            throw new NotSupportedException("GPU nie obsługuje wymaganej konwersji BGRA→NV12.");
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _outputTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
            Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
        });
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_outputTexture, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
            });
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true,
            new Vortice.RawRect(0, 0, SourceWidth, SourceHeight));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true,
            new Vortice.RawRect(0, 0, Width, Height));
        _encoder = new DirectHardwareH264Encoder(Width, Height, 60, targetKbps * 1000, _device);
    }

    public DxgiH264Frame Capture(uint timeoutMilliseconds)
    {
        var captureTimer = Stopwatch.StartNew();
        var result = _duplication.AcquireNextFrame(timeoutMilliseconds, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            if (!_hasEncodedInput) return EncodeLast(captureTimer.Elapsed.TotalMilliseconds, 0, 0, 0, 0);
            return new([], Width, Height, 0, 0, 0, 0, 0, false);
        }
        result.CheckError();
        try
        {
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var dirtyCount = DirtyRectangleCount(info.TotalMetadataBufferSize);
            if (dirtyCount == 0 && info.AccumulatedFrames == 0 && _hasEncodedInput)
                return new([], Width, Height, captureTimer.Elapsed.TotalMilliseconds, 0,
                    info.PointerPosition.Position.X, info.PointerPosition.Position.Y, 0, false);
            using var inputView = _videoDevice.CreateVideoProcessorInputView(texture, _enumerator,
                new VideoProcessorInputViewDescription
                {
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
                });
            var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
            _videoContext.VideoProcessorBlt(_processor, _outputView, info.AccumulatedFrames, [stream]).CheckError();
            _context.Flush();
            _hasEncodedInput = true;
            return EncodeLast(captureTimer.Elapsed.TotalMilliseconds, dirtyCount,
                info.PointerPosition.Position.X, info.PointerPosition.Position.Y, info.AccumulatedFrames);
        }
        finally { resource.Dispose(); _duplication.ReleaseFrame(); }
    }

    private DxgiH264Frame EncodeLast(double captureMs, int dirtyCount, int cursorX, int cursorY,
        uint accumulatedFrames)
    {
        var timer = Stopwatch.StartNew();
        var timestamp = (long)(_clock.Elapsed.TotalSeconds * 10_000_000);
        var bytes = _encoder.Encode(_outputTexture, timestamp);
        timer.Stop();
        return new(bytes, Width, Height, captureMs, timer.Elapsed.TotalMilliseconds,
            cursorX, cursorY, dirtyCount, ContainsIdr(bytes), accumulatedFrames);
    }

    private int DirtyRectangleCount(uint metadataBytes)
    {
        if (metadataBytes == 0) return 0;
        var size = Marshal.SizeOf<Vortice.RawRect>();
        var values = new Vortice.RawRect[Math.Max(1, (int)(metadataBytes / (uint)size))];
        var result = _duplication.GetFrameDirtyRects((uint)(values.Length * size), values, out var required);
        return result.Success ? (int)(required / (uint)size) : 0;
    }

    private static bool ContainsIdr(byte[] bytes)
    {
        for (var i = 0; i + 4 < bytes.Length; i++)
            if (bytes[i] == 0 && bytes[i + 1] == 0 &&
                ((bytes[i + 2] == 1 && (bytes[i + 3] & 31) == 5) ||
                 (bytes[i + 2] == 0 && bytes[i + 3] == 1 && (bytes[i + 4] & 31) == 5))) return true;
        return false;
    }

    public bool Matches(int maxWidth, int targetKbps) => TargetKbps == targetKbps && Width <= maxWidth &&
        Width > maxWidth - 32;

    public void Dispose()
    {
        _encoder.Dispose(); _outputView.Dispose(); _outputTexture.Dispose(); _processor.Dispose();
        _enumerator.Dispose(); _duplication.Dispose(); _videoContext.Dispose(); _videoDevice.Dispose();
        _context.Dispose(); _device.Dispose();
    }
}

internal sealed record DxgiH264Frame(byte[] Bytes, int Width, int Height, double CaptureMilliseconds,
    double EncodeMilliseconds, int CursorX, int CursorY, int DirtyRectangleCount, bool KeyFrame,
    uint AccumulatedFrames = 0);
