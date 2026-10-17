using System.Runtime.InteropServices;
using Kebuz.Recorder.Watching;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Recorder.Capture;

internal static class BorderlessProbe
{
    public static void Run(string executable)
    {
        var processes = new List<RunningProcess>();
        ProcessScanner.Snapshot(processes);

        var target = processes.FirstOrDefault(process =>
            string.Equals(process.Executable, executable, StringComparison.OrdinalIgnoreCase));

        if (target.Id == 0)
        {
            Console.WriteLine($"{executable} не запущен");
            return;
        }

        var game = GameWindow.FindLargest(target.Id);

        if (game == 0)
        {
            Console.WriteLine($"у {executable} (pid {target.Id}) нет подходящего окна");
            return;
        }

        Describe("окно игры", game);

        try
        {
            var item = CaptureTarget.Window(game);
            Console.WriteLine($"WGC на окно игры: {item.Size.Width}x{item.Size.Height}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WGC на окно игры: 0x{ex.HResult:X8} {ex.Message}");
        }

        ThumbnailHost? host = null;

        try
        {
            host = ThumbnailHost.Create(game);
            Console.WriteLine($"окно-хозяин поднято: {host.Width}x{host.Height}");
            Describe("окно-хозяин", host.Handle);

            var item = CaptureTarget.Window(host.Handle);
            Console.WriteLine($"WGC на окно-хозяин: {item.Size.Width}x{item.Size.Height}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"окно-хозяин или WGC на нём: 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            host?.Dispose();
        }
    }

    private static unsafe void Describe(string name, nint handle)
    {
        var hwnd = new HWND((void*)handle);

        PInvoke.GetWindowRect(hwnd, out var rect);
        PInvoke.GetClientRect(hwnd, out var client);

        var origin = new System.Drawing.Point(0, 0);
        PInvoke.ClientToScreen(hwnd, ref origin);

        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        PInvoke.GetMonitorInfo(monitor, ref info);

        var style = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var extended = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        Console.WriteLine(
            $"{name} 0x{handle:X}: окно ({rect.left},{rect.top})-({rect.right},{rect.bottom}) " +
            $"{rect.right - rect.left}x{rect.bottom - rect.top}, клиент {client.right}x{client.bottom} " +
            $"от ({origin.X},{origin.Y}), монитор ({info.rcMonitor.left},{info.rcMonitor.top})-" +
            $"({info.rcMonitor.right},{info.rcMonitor.bottom}), стиль 0x{style:X8}, расширенный 0x{extended:X8}, " +
            $"видимое {PInvoke.IsWindowVisible(hwnd)}, свёрнуто {PInvoke.IsIconic(hwnd)}");
    }
}
