using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace SirkAgent.Session;

internal sealed class DxgiDesktopCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private ID3D11Texture2D? _staging;
    private Bitmap? _lastBitmap;
    private int _width;
    private int _height;

    public DxgiDesktopCapture(int outputIndex)
    {
        D3D11CreateDevice(null!, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            Array.Empty<FeatureLevel>(), out _device, out _context).CheckError();
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs((uint)outputIndex, out var selectedOutput).CheckError();
        using var output = selectedOutput;
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);
    }

    public DxgiFrame Capture(uint timeoutMilliseconds)
    {
        var result = _duplication.AcquireNextFrame(timeoutMilliseconds, out var frameInfo, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout && _lastBitmap is not null)
            return new DxgiFrame((Bitmap)_lastBitmap.Clone(), [], [], 0, false, 0, 0);
        result.CheckError();
        try
        {
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var description = texture.Description;
            EnsureStaging((int)description.Width, (int)description.Height, description.Format);
            var moves = MoveRectangles(frameInfo.TotalMetadataBufferSize);
            var dirty = DirtyRectangles(frameInfo.TotalMetadataBufferSize);
            var regions = dirty.Concat(moves.Select(value =>
                    new Rectangle(value.X, value.Y, value.Width, value.Height)))
                .Select(value => Rectangle.Intersect(value, new Rectangle(0, 0, _width, _height)))
                .Where(value => value.Width > 0 && value.Height > 0)
                .ToArray();
            var fullCopy = _lastBitmap is null;
            if (fullCopy)
                _context.CopyResource(_staging!, texture);
            else
                foreach (var region in regions)
                    _context.CopySubresourceRegion(_staging!, 0,
                        (uint)region.X, (uint)region.Y, 0, texture, 0,
                        new Box(region.Left, region.Top, 0, region.Right, region.Bottom, 1));
            if (!fullCopy && regions.Length == 0)
                return new DxgiFrame((Bitmap)_lastBitmap!.Clone(), dirty, moves,
                    frameInfo.AccumulatedFrames, frameInfo.PointerPosition.Visible,
                    frameInfo.PointerPosition.Position.X, frameInfo.PointerPosition.Position.Y);
            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                _lastBitmap ??= new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
                var updated = fullCopy ? [new Rectangle(0, 0, _width, _height)] : regions;
                var data = _lastBitmap.LockBits(new Rectangle(0, 0, _width, _height),
                    ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    foreach (var region in updated)
                    {
                        for (var row = region.Top; row < region.Bottom; row++)
                        {
                            var source = IntPtr.Add(mapped.DataPointer,
                                checked((int)(row * mapped.RowPitch + region.Left * 4)));
                            var target = IntPtr.Add(data.Scan0, row * data.Stride + region.Left * 4);
                            CopyMemory(target, source, (nuint)(region.Width * 4));
                        }
                    }
                }
                finally { _lastBitmap.UnlockBits(data); }
                return new DxgiFrame((Bitmap)_lastBitmap.Clone(), dirty, moves, frameInfo.AccumulatedFrames,
                    frameInfo.PointerPosition.Visible, frameInfo.PointerPosition.Position.X,
                    frameInfo.PointerPosition.Position.Y);
            }
            finally { _context.Unmap(_staging!, 0); }
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    private Rectangle[] DirtyRectangles(uint metadataBytes)
    {
        if (metadataBytes == 0) return [];
        var capacity = Math.Max(1, (int)(metadataBytes / (uint)Marshal.SizeOf<Vortice.RawRect>()));
        var values = new Vortice.RawRect[capacity];
        var result = _duplication.GetFrameDirtyRects((uint)(values.Length * Marshal.SizeOf<Vortice.RawRect>()),
            values, out var required);
        if (result.Failure || required == 0) return [];
        return values.Take((int)(required / (uint)Marshal.SizeOf<Vortice.RawRect>()))
            .Select(value => Rectangle.FromLTRB(value.Left, value.Top, value.Right, value.Bottom)).ToArray();
    }

    private DesktopMove[] MoveRectangles(uint metadataBytes)
    {
        if (metadataBytes == 0) return [];
        var size = Marshal.SizeOf<OutduplMoveRect>();
        var values = new OutduplMoveRect[Math.Max(1, (int)(metadataBytes / (uint)size))];
        var result = _duplication.GetFrameMoveRects((uint)(values.Length * size), values, out var required);
        if (result.Failure || required == 0) return [];
        return values.Take((int)(required / (uint)size)).Select(value => new DesktopMove(
            value.SourcePoint.X, value.SourcePoint.Y,
            value.DestinationRect.Left, value.DestinationRect.Top,
            value.DestinationRect.Right - value.DestinationRect.Left,
            value.DestinationRect.Bottom - value.DestinationRect.Top)).ToArray();
    }

    private void EnsureStaging(int width, int height, Format format)
    {
        if (_staging is not null && _width == width && _height == height) return;
        _staging?.Dispose();
        _lastBitmap?.Dispose();
        _lastBitmap = null;
        _width = width;
        _height = height;
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, nuint length);
}

internal sealed record DxgiFrame(Bitmap Bitmap, Rectangle[] DirtyRectangles, DesktopMove[] MoveRectangles,
    uint AccumulatedFrames,
    bool PointerVisible, int PointerX, int PointerY) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}

internal sealed record DesktopMove(int SourceX, int SourceY, int X, int Y, int Width, int Height);
