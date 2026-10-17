using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Kebuz.Recorder.Capture;

internal sealed class TexturePool : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly uint width;
    private readonly uint height;
    private readonly Queue<ID3D11Texture2D> idle = new();
    private readonly Lock gate = new();

    private bool disposed;

    public TexturePool(GraphicsDevice device, uint width, uint height)
    {
        this.device = device;
        this.width = width;
        this.height = height;
    }

    public ID3D11Texture2D Rent()
    {
        lock (gate)
        {
            if (idle.Count > 0)
            {
                return idle.Dequeue();
            }
        }

        return Create();
    }

    public void Return(ID3D11Texture2D texture)
    {
        lock (gate)
        {
            if (!disposed)
            {
                idle.Enqueue(texture);
                return;
            }
        }

        Marshal.ReleaseComObject(texture);
    }

    private ID3D11Texture2D Create()
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };

        device.Device.CreateTexture2D(desc, null, out var texture);
        return texture;
    }

    public void Dispose()
    {
        ID3D11Texture2D[] pending;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pending = idle.ToArray();
            idle.Clear();
        }

        foreach (var texture in pending)
        {
            Marshal.ReleaseComObject(texture);
        }
    }
}
