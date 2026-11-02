using System.Diagnostics;
using System.Runtime.InteropServices;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Watching;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Recorder.Capture;

public static class CaptureSources
{
    public static unsafe IReadOnlyList<CaptureSource> List()
    {
        var sources = new List<CaptureSource>();
        PInvoke.EnumDisplayMonitors(HDC.Null, null, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEXW { monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFOEXW) } };
            if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                return true;
            }
            var device = info.szDevice.AsReadOnlySpan().SliceAtNull().ToString();
            var display = new DISPLAY_DEVICEW { cb = (uint)sizeof(DISPLAY_DEVICEW) };
            if (!PInvoke.EnumDisplayDevices(device, 0, ref display, 1))
            {
                return true;
            }
            var id = display.DeviceID.AsReadOnlySpan().SliceAtNull().ToString();
            if (id.Length == 0)
            {
                return true;
            }
            sources.Add(new CaptureSource("monitor:" + id, CaptureSourceKind.Monitor,
                display.DeviceString.AsReadOnlySpan().SliceAtNull().ToString(), device, null, 0,
                (long)(nint)monitor.Value, info.monitorInfo.rcMonitor.right - info.monitorInfo.rcMonitor.left,
                info.monitorInfo.rcMonitor.bottom - info.monitorInfo.rcMonitor.top, (info.monitorInfo.dwFlags & 1) != 0));
            return true;
        }, 0);
        PInvoke.EnumWindows((window, _) =>
        {
            if (!PInvoke.IsWindowVisible(window) || PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_OWNER) != HWND.Null)
            {
                return true;
            }
            PInvoke.GetWindowThreadProcessId(window, out var pid);
            var title = CaptureTarget.GetWindowTitle(window);
            if (title.Length == 0 || pid == 0)
            {
                return true;
            }
            var path = ProcessScanner.TryGetPath(pid);
            var executable = Path.GetFileName(path);
            if (path is null || string.Equals(executable, "KebuzReplay.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!PInvoke.GetWindowRect(window, out var rect) || rect.right <= rect.left || rect.bottom <= rect.top)
            {
                return true;
            }
            long born;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                born = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception)
            {
                return true;
            }
            sources.Add(new CaptureSource($"window:{pid}:{born}:{(nint)window.Value}", CaptureSourceKind.Window,
                title, executable!, path, pid, (long)(nint)window.Value, rect.right - rect.left, rect.bottom - rect.top));
            return true;
        }, 0);
        return sources;
    }
}
