using System.Diagnostics;
using System.Runtime.InteropServices;
using Kebuz.Recorder.Ipc;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Kebuz.Recorder.Watching;

internal readonly record struct DetectSample(
    uint ProcessId,
    string? Executable,
    double Coverage,
    bool GameFolder,
    double? Gpu,
    double Mean,
    TimeSpan Steady,
    string Verdict);

internal sealed class GameDetector : IDisposable
{
    public static readonly TimeSpan SteadyFor = TimeSpan.FromSeconds(10);

    public const double CoverageShare = 0.95;
    public const double GpuShare = 10;

    private const int MinimumWidth = 640;
    private const int MinimumHeight = 480;
    private const int Window = 10;

    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\",
        @"\Epic Games\",
        @"\GOG Games\",
        @"\GOG Galaxy\Games\",
        @"\Battle.net\",
        @"\XboxGames\",
        @"\Riot Games\",
        @"\Ubisoft Game Launcher\games\",
        @"\EA Games\"
    ];

    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "KebuzReplay.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "browser.exe",
        "iexplore.exe", "msedgewebview2.exe", "yandex.exe",
        "Code.exe", "devenv.exe", "rider64.exe", "idea64.exe", "pycharm64.exe", "clion64.exe", "webstorm64.exe",
        "sublime_text.exe", "notepad++.exe", "Cursor.exe",
        "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "OUTLOOK.EXE", "ONENOTE.EXE", "soffice.bin", "AcroRd32.exe",
        "Acrobat.exe",
        "obs64.exe", "obs32.exe", "vlc.exe", "mpc-hc64.exe", "mpc-hc.exe", "mpc-be64.exe", "PotPlayerMini64.exe",
        "PotPlayerMini.exe", "wmplayer.exe", "Video.UI.exe", "Microsoft.Media.Player.exe", "Spotify.exe",
        "Discord.exe", "Telegram.exe", "slack.exe", "Teams.exe", "ms-teams.exe", "Zoom.exe", "Skype.exe",
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "Battle.net.exe", "GalaxyClient.exe",
        "EADesktop.exe", "Origin.exe", "upc.exe", "UbisoftConnect.exe", "RiotClientUx.exe",
        "Photoshop.exe", "blender.exe", "Unity.exe", "UnrealEditor.exe", "UE4Editor.exe",
        "vmware.exe", "VirtualBoxVM.exe", "vmconnect.exe"
    };

    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private readonly uint self = (uint)Environment.ProcessId;
    private readonly HashSet<uint> asked = [];
    private readonly Queue<double> loads = new();

    private uint candidate;
    private string? path;
    private string? executable;
    private GpuLoad? gpu;
    private long steadySince;

    public DetectSample Tick(WatchedApps apps, IgnoredApps ignored, Action<DetectedGame> detected)
    {
        var hwnd = PInvoke.GetForegroundWindow();

        if (hwnd.IsNull)
        {
            Forget();
            return Idle("нет переднего окна");
        }

        PInvoke.GetWindowThreadProcessId(hwnd, out var id);

        if (id == 0 || id == self)
        {
            Forget();
            return Idle("своё окно");
        }

        if (id != candidate)
        {
            Forget();
            candidate = id;
            path = ProcessScanner.TryGetPath(id);
            executable = path is null ? null : Path.GetFileName(path);
        }

        if (path is null || executable is null)
        {
            return Pause("путь к exe не читается");
        }

        if (Skip(apps, ignored) is { } reason)
        {
            return Pause(reason);
        }

        if (PInvoke.IsIconic(hwnd))
        {
            return Pause("окно свёрнуто");
        }

        var (coverage, large) = Measure(hwnd);
        var folder = InGameFolder(path);

        if (coverage < CoverageShare && !(folder && large))
        {
            return Pause("окно не на весь экран", coverage, folder);
        }

        gpu ??= new GpuLoad(id);

        var load = gpu.Sample();

        if (steadySince == 0)
        {
            steadySince = Stopwatch.GetTimestamp();
        }

        loads.Enqueue(load ?? 0);

        while (loads.Count > Window)
        {
            loads.Dequeue();
        }

        var steady = Stopwatch.GetElapsedTime(steadySince);
        var mean = loads.Average();

        if (steady < SteadyFor)
        {
            return new DetectSample(id, executable, coverage, folder, load, mean, steady, "держится");
        }

        if (mean < GpuShare)
        {
            return new DetectSample(id, executable, coverage, folder, load, mean, steady, "мало 3D");
        }

        asked.Add(id);
        detected(new DetectedGame(executable, path, WatchedApps.TitleOf(path)));

        var sample = new DetectSample(id, executable, coverage, folder, load, mean, steady, "игра");
        Rest();

        return sample;
    }

    public void Forget()
    {
        Rest();
        gpu?.Dispose();
        gpu = null;
        candidate = 0;
        path = null;
        executable = null;
    }

    public static bool InGameFolder(string path)
    {
        foreach (var folder in GameFolders)
        {
            if (path.Contains(folder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string? Skip(WatchedApps apps, IgnoredApps ignored)
    {
        if (NotGames.Contains(executable!))
        {
            return "точно не игра";
        }

        if (path!.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return "системный exe";
        }

        if (asked.Contains(candidate))
        {
            return "уже спрошено";
        }

        if (apps.Match(executable!, () => path) is not null)
        {
            return "в списке игр";
        }

        if (ignored.Contains(executable!, path))
        {
            return "в не-играх";
        }

        return null;
    }

    private static (double Coverage, bool Large) Measure(HWND hwnd)
    {
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            return (0, false);
        }

        var large = rect.right - rect.left >= MinimumWidth && rect.bottom - rect.top >= MinimumHeight;
        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (monitor.IsNull || !PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return (0, large);
        }

        var screen = info.rcMonitor;
        var width = Math.Min(rect.right, screen.right) - Math.Max(rect.left, screen.left);
        var height = Math.Min(rect.bottom, screen.bottom) - Math.Max(rect.top, screen.top);
        var area = (double)(screen.right - screen.left) * (screen.bottom - screen.top);

        if (width <= 0 || height <= 0 || area <= 0)
        {
            return (0, large);
        }

        return ((double)width * height / area, large);
    }

    private DetectSample Idle(string verdict)
    {
        return new DetectSample(0, null, 0, false, null, 0, TimeSpan.Zero, verdict);
    }

    private DetectSample Pause(string verdict, double coverage = 0, bool folder = false)
    {
        Rest();
        return new DetectSample(candidate, executable, coverage, folder, null, 0, TimeSpan.Zero, verdict);
    }

    private void Rest()
    {
        steadySince = 0;
        loads.Clear();
    }

    public void Dispose()
    {
        Forget();
    }
}
