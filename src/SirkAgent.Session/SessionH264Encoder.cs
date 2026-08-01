using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SharpMediaFoundationInterop.Transforms.H264;
using Windows.Win32;
using MfColorConverter = SharpMediaFoundationInterop.Transforms.Colors.ColorConverter;

namespace SirkAgent.Session;

internal sealed class SessionH264Encoder : IDisposable
{
    private readonly MfColorConverter _converter;
    private readonly H264Encoder _encoder;
    private readonly byte[] _rgb;
    private byte[] _nv12;
    private byte[] _output;
    private long _timestamp;
    private readonly int _bitrate;
    public int Width { get; }
    public int Height { get; }
    public bool LastWasKeyFrame { get; private set; }

    public SessionH264Encoder(int width, int height, int bitrate)
    {
        Width = width; Height = height; _bitrate = bitrate;
        _converter = new MfColorConverter(PInvoke.MFVideoFormat_RGB24, PInvoke.MFVideoFormat_NV12,
            (uint)width, (uint)height);
        _converter.Initialize();
        _encoder = new H264Encoder((uint)width, (uint)height, 60, 1, (uint)bitrate);
        _encoder.Initialize();
        _rgb = new byte[checked(width * height * 3)];
        _nv12 = new byte[_converter.OutputSize];
        _output = new byte[_encoder.OutputSize];
    }

    public bool Matches(int width, int height, int bitrate) =>
        Width == width && Height == height && _bitrate == bitrate;

    public byte[] Encode(Bitmap bitmap)
    {
        CopyRgb(bitmap);
        if (!_converter.ProcessInput(_rgb, _timestamp) || !_converter.ProcessOutput(ref _nv12, out _)) return [];
        if (!_encoder.ProcessInput(_nv12, _timestamp)) return [];
        _timestamp += 10_000_000L / 60;
        using var result = new MemoryStream();
        while (_encoder.ProcessOutput(ref _output, out var length)) result.Write(_output, 0, checked((int)length));
        var bytes = result.ToArray();
        LastWasKeyFrame = ContainsIdr(bytes);
        return bytes;
    }

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
