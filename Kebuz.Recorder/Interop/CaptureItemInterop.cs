using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Kebuz.Recorder.Interop;

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    nint CreateForWindow(nint window, in Guid iid);

    nint CreateForMonitor(nint monitor, in Guid iid);
}

internal static class CaptureItemInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateForWindow(nint hwnd)
    {
        var raw = Factory().CreateForWindow(hwnd, GraphicsCaptureItemIid);
        return FromAbi(raw);
    }

    public static GraphicsCaptureItem CreateForMonitor(nint hmonitor)
    {
        var raw = Factory().CreateForMonitor(hmonitor, GraphicsCaptureItemIid);
        return FromAbi(raw);
    }

    private static IGraphicsCaptureItemInterop Factory()
    {
        return ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem")
            .AsInterface<IGraphicsCaptureItemInterop>();
    }

    private static GraphicsCaptureItem FromAbi(nint raw)
    {
        if (raw == 0)
        {
            throw new InvalidOperationException("GraphicsCaptureItem не был создан");
        }

        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }
}
