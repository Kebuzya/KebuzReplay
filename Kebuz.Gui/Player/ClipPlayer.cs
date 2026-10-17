using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Kebuz.Gui.Player;

public sealed class ClipPlayer : IDisposable
{
    private const uint ApiVersion = 0x00020070;
    private const uint NoSocket = 1;
    private const uint SdkVersion = 7;
    private const uint FormatBgra = 87;

    private readonly Dispatcher dispatcher;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IMFDXGIDeviceManager manager;
    private readonly IMFMediaEngine engine;
    private readonly SharedSurface surface;
    private readonly Notify notify;

    private bool ready;
    private bool rebind;
    private bool disposed;

    private ClipPlayer(
        Dispatcher dispatcher,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IMFDXGIDeviceManager manager,
        SharedSurface surface)
    {
        this.dispatcher = dispatcher;
        this.device = device;
        this.context = context;
        this.manager = manager;
        this.surface = surface;

        notify = new Notify(Notified);

        Image.IsFrontBufferAvailableChanged += (_, _) =>
        {
            if (!Image.IsFrontBufferAvailable)
            {
                return;
            }

            rebind = true;
            Revived?.Invoke();
        };

        PInvoke.MFCreateAttributes(out var attributes, 4).ThrowOnFailure();
        attributes.SetUnknown(MediaEngineAttributes.Callback, notify);
        attributes.SetUnknown(MediaEngineAttributes.DxgiManager, manager);
        attributes.SetUINT32(MediaEngineAttributes.OutputFormat, FormatBgra);

        PInvoke.CoCreateInstance<IMFMediaEngineClassFactory>(
            MediaEngineAttributes.FactoryClass,
            null,
            CLSCTX.CLSCTX_INPROC_SERVER,
            out var factory).ThrowOnFailure();

        factory.CreateInstance(0, attributes, out engine);
        Marshal.ReleaseComObject(factory);
        Marshal.ReleaseComObject(attributes);
    }

    public event Action? Loaded;

    public event Action? Ended;

    public event Action<string>? Failed;

    public event Action? Revived;

    public bool IsSeeking => engine.IsSeeking();

    public D3DImage Image { get; } = new();

    public bool IsReady => ready;

    public double Duration
    {
        get
        {
            var value = engine.GetDuration();
            return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
        }
    }

    public double Position
    {
        get
        {
            var value = engine.GetCurrentTime();
            return double.IsNaN(value) || value < 0 ? 0 : value;
        }
    }

    public bool IsPaused => engine.IsPaused();

    public static ClipPlayer Create()
    {
        PInvoke.MFStartup(ApiVersion, NoSocket).ThrowOnFailure();

        ReadOnlySpan<D3D_FEATURE_LEVEL> levels =
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
            levels,
            SdkVersion,
            out var device,
            out var context).ThrowOnFailure();

        ((ID3D10Multithread)device).SetMultithreadProtected(true);

        PInvoke.MFCreateDXGIDeviceManager(out var token, out var manager).ThrowOnFailure();
        manager.ResetDevice(device, token);

        return new ClipPlayer(
            Dispatcher.CurrentDispatcher,
            device,
            context,
            manager,
            SharedSurface.Create(device));
    }

    public void Open(string path)
    {
        ready = false;

        var text = Marshal.StringToBSTR(path);

        try
        {
            engine.SetSource((BSTR)text);
        }
        finally
        {
            Marshal.FreeBSTR(text);
        }
    }

    public void Play()
    {
        engine.Play();
    }

    public void Pause()
    {
        engine.Pause();
    }

    public void Seek(double seconds)
    {
        engine.SetCurrentTime(Math.Max(0, seconds));
    }

    public void SetVolume(double level)
    {
        engine.SetVolume(Math.Clamp(level, 0, 1));
    }

    public unsafe bool Draw()
    {
        if (!ready || disposed || !Image.IsFrontBufferAvailable)
        {
            return false;
        }

        uint width;
        uint height;
        engine.GetNativeVideoSize(&width, &height);

        if (width == 0 || height == 0)
        {
            return false;
        }

        var fresh = surface.Resize(width, height);

        if (surface.Target is not { } target)
        {
            return false;
        }

        engine.OnVideoStreamTick(out _);

        var box = new RECT { left = 0, top = 0, right = (int)width, bottom = (int)height };

        Image.Lock();

        try
        {
            if (fresh || rebind)
            {
                Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.Pointer, true);
                rebind = false;
            }

            engine.TransferVideoFrame(target, null, &box, null);
            context.Flush();
            Image.AddDirtyRect(new Int32Rect(0, 0, (int)width, (int)height));
        }
        finally
        {
            Image.Unlock();
        }

        return true;
    }

    public bool Capture(out uint width, out uint height, out byte[] pixels)
    {
        width = surface.Width;
        height = surface.Height;
        pixels = [];

        if (surface.Target is not { } target || width == 0 || height == 0)
        {
            return false;
        }

        return Read(target, width, height, out pixels);
    }

    private unsafe bool Read(ID3D11Texture2D target, uint width, uint height, out byte[] pixels)
    {
        pixels = [];

        var desc = default(D3D11_TEXTURE2D_DESC);
        target.GetDesc(&desc);

        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.MiscFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;

        ID3D11Texture2D_unmanaged* raw;
        device.CreateTexture2D(&desc, null, &raw);
        var staging = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)raw);
        Marshal.Release((nint)raw);

        try
        {
            context.CopyResource((ID3D11Resource)staging, (ID3D11Resource)target);

            var map = default(D3D11_MAPPED_SUBRESOURCE);
            context.Map((ID3D11Resource)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &map);

            try
            {
                pixels = new byte[width * height * 4];

                for (var row = 0u; row < height; row++)
                {
                    var line = new ReadOnlySpan<byte>((byte*)map.pData + row * map.RowPitch, (int)(width * 4));
                    line.CopyTo(pixels.AsSpan((int)(row * width * 4)));
                }
            }
            finally
            {
                context.Unmap((ID3D11Resource)staging, 0);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(staging);
        }

        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ready = false;

        engine.Shutdown();
        Marshal.ReleaseComObject(engine);
        surface.Dispose();
        Marshal.ReleaseComObject(manager);
        Marshal.ReleaseComObject(context);
        Marshal.ReleaseComObject(device);
    }

    private void Notified(uint code, uint param)
    {
        if (disposed)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (disposed)
            {
                return;
            }

            switch ((MF_MEDIA_ENGINE_EVENT)code)
            {
                case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_LOADEDDATA:
                    ready = true;
                    Loaded?.Invoke();
                    break;

                case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ENDED:
                    Ended?.Invoke();
                    break;

                case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ERROR:
                    ready = false;
                    Failed?.Invoke($"{param:X8}");
                    break;
            }
        });
    }

    private sealed class Notify : IMFMediaEngineNotify
    {
        private readonly Action<uint, uint> sink;

        public Notify(Action<uint, uint> sink)
        {
            this.sink = sink;
        }

        public void EventNotify(uint @event, nuint param1, uint param2)
        {
            sink(@event, param2);
        }
    }
}

internal static class MediaEngineAttributes
{
    public static readonly Guid FactoryClass = new("b44392da-499b-446b-a4cb-005fead0e6d5");

    public static readonly Guid Callback = new("c60381b8-83a4-41f8-a3d0-de05076849a9");

    public static readonly Guid DxgiManager = new("065702da-1094-486d-8617-ee7cc4ee4648");

    public static readonly Guid OutputFormat = new("5066893c-8cf9-42bc-8b8a-472212e52726");

    public static unsafe void SetUnknown(this IMFAttributes attributes, Guid key, object value)
    {
        attributes.SetUnknown(&key, value);
    }

    public static unsafe void SetUINT32(this IMFAttributes attributes, Guid key, uint value)
    {
        attributes.SetUINT32(&key, value);
    }
}
