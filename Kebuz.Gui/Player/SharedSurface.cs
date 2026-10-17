using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D9;
using Windows.Win32.Graphics.Direct3D11;

namespace Kebuz.Gui.Player;

internal sealed class SharedSurface : IDisposable
{
    private const uint SdkVersion = 32;
    private const uint AdapterDefault = 0;
    private const uint HardwareVertexProcessing = 0x40;
    private const uint Multithreaded = 0x4;
    private const uint FpuPreserve = 0x2;
    private const uint UsageRenderTarget = 0x1;
    private const uint PresentIntervalImmediate = 0x80000000;

    private readonly IDirect3D9Ex factory;
    private readonly IDirect3DDevice9Ex device9;
    private readonly ID3D11Device device11;

    private IDirect3DTexture9? texture9;
    private IDirect3DSurface9? surface9;
    private ID3D11Texture2D? texture11;
    private nint pointer;

    private SharedSurface(IDirect3D9Ex factory, IDirect3DDevice9Ex device9, ID3D11Device device11)
    {
        this.factory = factory;
        this.device9 = device9;
        this.device11 = device11;
    }

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public nint Pointer => pointer;

    public ID3D11Texture2D? Target => texture11;

    public static unsafe SharedSurface Create(ID3D11Device device11)
    {
        PInvoke.Direct3DCreate9Ex(SdkVersion, out var factory).ThrowOnFailure();

        var present = new D3DPRESENT_PARAMETERS
        {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = D3DFORMAT.D3DFMT_UNKNOWN,
            BackBufferCount = 1,
            MultiSampleType = D3DMULTISAMPLE_TYPE.D3DMULTISAMPLE_NONE,
            SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
            hDeviceWindow = PInvoke.GetDesktopWindow(),
            Windowed = true,
            PresentationInterval = PresentIntervalImmediate
        };

        factory.CreateDeviceEx(
            AdapterDefault,
            D3DDEVTYPE.D3DDEVTYPE_HAL,
            present.hDeviceWindow,
            HardwareVertexProcessing | Multithreaded | FpuPreserve,
            &present,
            null,
            out var device9);

        return new SharedSurface(factory, device9, device11);
    }

    public unsafe bool Resize(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return false;
        }

        if (texture11 is not null && width == Width && height == Height)
        {
            return false;
        }

        Release();

        var shared = HANDLE.Null;
        device9.CreateTexture(
            width,
            height,
            1,
            UsageRenderTarget,
            D3DFORMAT.D3DFMT_X8R8G8B8,
            D3DPOOL.D3DPOOL_DEFAULT,
            out var fresh,
            &shared);

        texture9 = fresh;
        fresh.GetSurfaceLevel(0, out var level);
        surface9 = level;
        pointer = Marshal.GetComInterfaceForObject(level, typeof(IDirect3DSurface9));

        var iid = typeof(ID3D11Texture2D).GUID;
        void* opened;
        device11.OpenSharedResource(shared, &iid, &opened);
        texture11 = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)opened);
        Marshal.Release((nint)opened);

        Width = width;
        Height = height;

        return true;
    }

    public void Dispose()
    {
        Release();
        Marshal.ReleaseComObject(device9);
        Marshal.ReleaseComObject(factory);
    }

    private void Release()
    {
        if (pointer != 0)
        {
            Marshal.Release(pointer);
            pointer = 0;
        }

        if (texture11 is not null)
        {
            Marshal.ReleaseComObject(texture11);
            texture11 = null;
        }

        if (surface9 is not null)
        {
            Marshal.ReleaseComObject(surface9);
            surface9 = null;
        }

        if (texture9 is not null)
        {
            Marshal.ReleaseComObject(texture9);
            texture9 = null;
        }

        Width = 0;
        Height = 0;
    }
}
