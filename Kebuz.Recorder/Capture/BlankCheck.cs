using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Kebuz.Recorder.Capture;

internal sealed class BlankCheck : IDisposable
{
    private const uint Patch = 8;
    private const uint Patches = 5;
    private const int DarkLimit = 4;

    private readonly GraphicsDevice device;
    private readonly ID3D11Texture2D staging;
    private bool disposed;

    public BlankCheck(GraphicsDevice device)
    {
        this.device = device;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = Patch * Patches,
            Height = Patch,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        device.Device.CreateTexture2D(desc, null, out staging);
    }

    public unsafe bool IsBlank(ID3D11Texture2D source, uint width, uint height)
    {
        if (width < Patch * 4 || height < Patch * 4)
        {
            return false;
        }

        var points = new (uint X, uint Y)[]
        {
            (width / 2, height / 2),
            (width / 4, height / 4),
            (width * 3 / 4, height / 4),
            (width / 4, height * 3 / 4),
            (width * 3 / 4, height * 3 / 4)
        };

        for (var i = 0u; i < Patches; i++)
        {
            var box = new D3D11_BOX
            {
                left = points[i].X,
                top = points[i].Y,
                front = 0,
                right = points[i].X + Patch,
                bottom = points[i].Y + Patch,
                back = 1
            };

            device.Context.CopySubresourceRegion((ID3D11Resource)staging, 0, i * Patch, 0, 0, (ID3D11Resource)source, 0, &box);
        }

        device.Context.Map((ID3D11Resource)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);

        try
        {
            for (var y = 0u; y < Patch; y++)
            {
                var row = (byte*)mapped.pData + (y * mapped.RowPitch);

                for (var x = 0u; x < Patch * Patches; x++)
                {
                    var pixel = row + (x * 4);

                    if (pixel[0] > DarkLimit || pixel[1] > DarkLimit || pixel[2] > DarkLimit)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            device.Context.Unmap((ID3D11Resource)staging, 0);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Marshal.ReleaseComObject(staging);
    }
}
