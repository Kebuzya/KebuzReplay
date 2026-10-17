using System.Runtime.InteropServices;
using System.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Kebuz.Gui.Shell;

internal static class ToastCorner
{
    public const double Gap = 12;

    public static void Place(Window window)
    {
        var target = PresentationSource.FromVisual(window)?.CompositionTarget;

        if (target is null)
        {
            return;
        }

        var front = PInvoke.GetForegroundWindow();
        var monitor = front.IsNull
            ? PInvoke.MonitorFromWindow(HWND.Null, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY)
            : PInvoke.MonitorFromWindow(front, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        if (monitor.IsNull)
        {
            return;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var corner = target.TransformFromDevice.Transform(new Point(info.rcWork.right, info.rcWork.bottom));

        window.Left = corner.X - window.Width - Gap;
        window.Top = corner.Y - window.Height - Gap;
    }
}
