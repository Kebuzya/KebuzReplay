using Kebuz.Recorder.Ipc;

namespace Kebuz.Recorder.Watching;

internal sealed class DetectedGameQueue
{
    private readonly List<DetectedGame> items = [];

    public bool Add(DetectedGame game)
    {
        if (items.Any(item => item.ProcessId == game.ProcessId
            || string.Equals(item.Path, game.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        items.Add(game);
        return true;
    }

    public bool Refresh(WatchedApps apps, IgnoredApps ignored, Func<uint, bool> alive, bool enabled)
    {
        return items.RemoveAll(game => !enabled || !alive(game.ProcessId)
            || apps.Match(game.Executable, () => game.Path) is not null
            || ignored.Contains(game.Executable, game.Path)) > 0;
    }

    public IReadOnlyList<DetectedGame> Snapshot()
    {
        return items.ToArray();
    }
}
