using System.IO;
using System.Text;
using Kebuz.Recorder.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Gui.Gallery;

public readonly record struct RecycleResult(int Code, bool Aborted);

public static class ShellFiles
{
    private const uint DeleteFiles = 3;
    private const nint LaunchFloor = 32;

    private const FILEOPERATION_FLAGS Quietly =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO |
        FILEOPERATION_FLAGS.FOF_NOCONFIRMATION |
        FILEOPERATION_FLAGS.FOF_NOERRORUI |
        FILEOPERATION_FLAGS.FOF_SILENT |
        FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR;

    public static unsafe RecycleResult Recycle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return new RecycleResult(0, false);
        }

        var names = new StringBuilder();

        foreach (var path in paths)
        {
            names.Append(path).Append('\0');
        }

        names.Append('\0');

        fixed (char* buffer = names.ToString())
        {
            var operation = new SHFILEOPSTRUCTW
            {
                wFunc = DeleteFiles,
                pFrom = new PCZZWSTR(buffer),
                fFlags = (ushort)Quietly
            };

            var code = PInvoke.SHFileOperation(ref operation);
            var aborted = (bool)operation.fAnyOperationsAborted;

            if (code != 0 || aborted)
            {
                Log.Error($"корзина не приняла клипов {paths.Count}: код {code}, прервано {aborted}");
            }

            return new RecycleResult(code, aborted);
        }
    }

    public static unsafe bool Reveal(string clip)
    {
        if (File.Exists(clip) && Select(clip))
        {
            return true;
        }

        var folder = Path.GetDirectoryName(clip);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Log.Error($"папку клипа не открыть, её нет: {clip}");
            return false;
        }

        fixed (char* verb = "open")
        fixed (char* target = folder)
        {
            var launched = PInvoke.ShellExecute(HWND.Null, verb, target, default, default, SHOW_WINDOW_CMD.SW_SHOWNORMAL);
            var code = (nint)launched.Value;

            if (code > LaunchFloor)
            {
                return true;
            }

            Log.Error($"проводник не открыл папку {folder}, код {code}");
            return false;
        }
    }

    private static unsafe bool Select(string clip)
    {
        var item = PInvoke.ILCreateFromPath(clip);

        if (item is null)
        {
            return false;
        }

        try
        {
            var result = PInvoke.SHOpenFolderAndSelectItems(item, 0, null, 0);

            if (result.Succeeded)
            {
                return true;
            }

            Log.Error($"проводник не выделил клип {clip}, код {result.Value:X8}");
            return false;
        }
        finally
        {
            PInvoke.ILFree(item);
        }
    }
}
