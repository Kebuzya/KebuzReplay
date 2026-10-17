using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Kebuz.Recorder.Capture;

internal sealed record ShotImage(uint Width, uint Height, byte[] Png, bool Dark, uint SourceWidth, uint SourceHeight);

internal static class FrameShot
{
    public const uint TargetWidth = 256;

    private const int Taps = 4;
    private const int DarkLimit = 8;

    public static unsafe ShotImage Take(GraphicsDevice device, ID3D11Texture2D source, uint width, uint height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        device.Device.CreateTexture2D(desc, null, out var staging);

        try
        {
            var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = width, bottom = height, back = 1 };
            device.Context.CopySubresourceRegion((ID3D11Resource)staging, 0, 0, 0, 0, (ID3D11Resource)source, 0, &box);
            device.Context.Map((ID3D11Resource)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);

            try
            {
                var pixels = new ReadOnlySpan<byte>(mapped.pData, (int)(mapped.RowPitch * height));
                var shrunk = Shrink(pixels, (int)mapped.RowPitch, width, height, TargetWidth);

                return new ShotImage(
                    shrunk.Width,
                    shrunk.Height,
                    PngWriter.Write(shrunk.Rgb, shrunk.Width, shrunk.Height),
                    shrunk.Dark,
                    width,
                    height);
            }
            finally
            {
                device.Context.Unmap((ID3D11Resource)staging, 0);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(staging);
        }
    }

    public static (byte[] Rgb, uint Width, uint Height, bool Dark) Shrink(
        ReadOnlySpan<byte> bgra,
        int pitch,
        uint width,
        uint height,
        uint targetWidth)
    {
        var outWidth = Math.Min(targetWidth, width);
        var outHeight = Math.Max(1u, (uint)Math.Round((double)height * outWidth / width));
        var rgb = new byte[outWidth * outHeight * 3];
        var dark = true;

        for (var ty = 0u; ty < outHeight; ty++)
        {
            var top = (double)ty * height / outHeight;
            var span = (double)height / outHeight;

            for (var tx = 0u; tx < outWidth; tx++)
            {
                var left = (double)tx * width / outWidth;
                var reach = (double)width / outWidth;
                int blue = 0, green = 0, red = 0;

                for (var j = 0; j < Taps; j++)
                {
                    var y = Math.Min(height - 1, (uint)(top + (span * (j + 0.5) / Taps)));

                    for (var i = 0; i < Taps; i++)
                    {
                        var x = Math.Min(width - 1, (uint)(left + (reach * (i + 0.5) / Taps)));
                        var at = (int)(y * pitch + x * 4);

                        blue += bgra[at];
                        green += bgra[at + 1];
                        red += bgra[at + 2];
                    }
                }

                var taps = Taps * Taps;
                var to = (int)((ty * outWidth + tx) * 3);

                rgb[to] = (byte)(red / taps);
                rgb[to + 1] = (byte)(green / taps);
                rgb[to + 2] = (byte)(blue / taps);

                if (rgb[to] > DarkLimit || rgb[to + 1] > DarkLimit || rgb[to + 2] > DarkLimit)
                {
                    dark = false;
                }
            }
        }

        return (rgb, outWidth, outHeight, dark);
    }
}
