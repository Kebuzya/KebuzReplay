using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Recorder.Watching;

internal static class GameWindow
{
    private const int MinimumWidth = 640;
    private const int MinimumHeight = 480;

    public static nint FindLargest(uint processId)
    {
        nint best = 0;
        var bestArea = 0L;

        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null)
            {
                return true;
            }

            PInvoke.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != processId)
            {
                return true;
            }

            if (IsConsole(hwnd) || !PInvoke.GetWindowRect(hwnd, out var rect))
            {
                return true;
            }

            var width = rect.right - rect.left;
            var height = rect.bottom - rect.top;

            if (width < MinimumWidth || height < MinimumHeight)
            {
                return true;
            }

            var area = (long)width * height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, 0);

        return best;
    }

    public static long Area(nint window)
    {
        var hwnd = new HWND(window);

        if (window == 0 || !PInvoke.IsWindowVisible(hwnd) || !PInvoke.GetWindowRect(hwnd, out var rect))
        {
            return 0;
        }

        return (long)(rect.right - rect.left) * (rect.bottom - rect.top);
    }

    private static bool IsConsole(HWND hwnd)
    {
        Span<char> name = stackalloc char[32];
        var length = PInvoke.GetClassName(hwnd, name);

        return length > 0 && name[..length].SequenceEqual("ConsoleWindowClass");
    }
}
