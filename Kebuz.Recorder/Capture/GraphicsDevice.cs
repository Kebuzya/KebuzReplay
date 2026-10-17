using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using WinRT;

namespace Kebuz.Recorder.Capture;

internal sealed class GraphicsDevice : IDisposable
{
    private const uint SdkVersion = 7;
    private const int DeviceRemoved = unchecked((int)0x887A0005);

    private readonly long simulateAt;

    private bool disposed;

    private GraphicsDevice(
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDirect3DDevice winRtDevice,
        long simulateAt)
    {
        Device = device;
        Context = context;
        WinRtDevice = winRtDevice;
        this.simulateAt = simulateAt;
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public IDirect3DDevice WinRtDevice { get; }

    public unsafe void CopyRegion(ID3D11Texture2D target, ID3D11Texture2D source, uint width, uint height)
    {
        var box = new D3D11_BOX
        {
            left = 0,
            top = 0,
            front = 0,
            right = width,
            bottom = height,
            back = 1
        };

        Context.CopySubresourceRegion((ID3D11Resource)target, 0, 0, 0, 0, (ID3D11Resource)source, 0, &box);
    }

    public int? LossReason()
    {
        if (simulateAt != 0 && Stopwatch.GetTimestamp() >= simulateAt)
        {
            return DeviceRemoved;
        }

        try
        {
            Device.GetDeviceRemovedReason();
            return null;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    public static GraphicsDevice Create(TimeSpan simulateLossAfter = default)
    {
        ReadOnlySpan<D3D_FEATURE_LEVEL> featureLevels =
        [
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1
        ];

        const D3D11_CREATE_DEVICE_FLAG flags =
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT |
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;

        PInvoke.D3D11CreateDevice(
            null,
            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            HMODULE.Null,
            flags,
            featureLevels,
            SdkVersion,
            out var device,
            out var context).ThrowOnFailure();

        ((ID3D10Multithread)device).SetMultithreadProtected(true);

        PInvoke.CreateDirect3D11DeviceFromDXGIDevice((IDXGIDevice)device, out var inspectable).ThrowOnFailure();

        var abi = Marshal.GetIUnknownForObject(inspectable);
        IDirect3DDevice winRtDevice;
        try
        {
            winRtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }

        var simulateAt = simulateLossAfter > TimeSpan.Zero
            ? Stopwatch.GetTimestamp() + (long)(simulateLossAfter.TotalSeconds * Stopwatch.Frequency)
            : 0;

        return new GraphicsDevice(device, context, winRtDevice, simulateAt);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        WinRtDevice.Dispose();
        Marshal.ReleaseComObject(Context);
        Marshal.ReleaseComObject(Device);
    }
}
