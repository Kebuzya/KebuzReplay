using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Recorder.Watching;

public sealed record RunningApp(string Title, string Executable, string Path)
{
    public override string ToString()
    {
        return $"{Title}, {Executable}";
    }
}

public static class RunningWindows
{
    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

    public static IReadOnlyList<RunningApp> List()
    {
        var self = (uint)Environment.ProcessId;
        var titles = new Dictionary<uint, (string Title, long Area)>();

        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null)
            {
                return true;
            }

            PInvoke.GetWindowThreadProcessId(hwnd, out var owner);

            if (owner == 0 || owner == self)
            {
                return true;
            }

            var title = Title(hwnd);

            if (title.Length == 0)
            {
                return true;
            }

            var area = PInvoke.GetWindowRect(hwnd, out var rect)
                ? (long)(rect.right - rect.left) * (rect.bottom - rect.top)
                : 0;

            if (!titles.TryGetValue(owner, out var known) || area > known.Area)
            {
                titles[owner] = (title, area);
            }

            return true;
        }, 0);

        var found = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, window) in titles)
        {
            var path = ProcessScanner.TryGetPath(id);

            if (path is null || path.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var executable = System.IO.Path.GetFileName(path);

            if (string.Equals(executable, "KebuzReplay.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found.TryAdd(path, new RunningApp(window.Title, executable, path));
        }

        return [.. found.Values.OrderBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static unsafe string Title(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);

        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];

        fixed (char* text = buffer)
        {
            var copied = PInvoke.GetWindowText(hwnd, text, buffer.Length);
            return new string(buffer, 0, Math.Max(0, copied)).Trim();
        }
    }
}
