using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.WinRT.Direct3D11;
using WinRT;

namespace Kebuz.Recorder.Capture;

internal readonly record struct CapturedFrame(ID3D11Texture2D Texture, TimeSpan Timestamp, SizeInt32 ContentSize);

internal sealed class FrameCapture : IDisposable
{
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;
    private readonly GraphicsDevice device;
    private readonly Action<CapturedFrame> onFrame;
    private bool disposed;

    public FrameCapture(GraphicsDevice device, GraphicsCaptureItem item, Action<CapturedFrame> onFrame)
    {
        this.onFrame = onFrame;
        this.device = device;
        Size = item.Size;

        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            Size);
        framePool.FrameArrived += OnFrameArrived;
        session = framePool.CreateCaptureSession(item);
        Border = CaptureBorder.TryHide(session);
    }

    public SizeInt32 Size { get; private set; }

    public string Border { get; }

    public void Start()
    {
        session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        access.GetInterface<ID3D11Texture2D>(out var texture);

        try
        {
            onFrame(new CapturedFrame(texture, frame.SystemRelativeTime, frame.ContentSize));
        }
        finally
        {
            Marshal.ReleaseComObject(texture);
        }

        var content = frame.ContentSize;

        if (content.Width > 0 && content.Height > 0 && (content.Width != Size.Width || content.Height != Size.Height))
        {
            Size = content;
            sender.Recreate(device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        framePool.FrameArrived -= OnFrameArrived;
        session.Dispose();
        framePool.Dispose();
    }
}
