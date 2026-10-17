using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Kebuz.Recorder.Interop;

namespace Kebuz.Recorder.Capture;

internal static class CaptureTarget
{
    public static GraphicsCaptureItem PrimaryMonitor()
    {
        var monitor = PInvoke.MonitorFromWindow(PInvoke.GetDesktopWindow(), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        return CaptureItemInterop.CreateForMonitor(monitor);
    }

    public static GraphicsCaptureItem Window(nint hwnd)
    {
        return CaptureItemInterop.CreateForWindow(hwnd);
    }

    public static nint FindWindowByTitle(string fragment)
    {
        nint found = 0;

        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (title.Length == 0 || !title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            found = hwnd;
            return false;
        }, 0);

        return found;
    }

    public static unsafe string GetWindowTitle(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var copied = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
        }
    }
}
