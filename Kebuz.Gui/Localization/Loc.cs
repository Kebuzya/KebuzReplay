using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Localization;

public sealed class Loc : INotifyPropertyChanged
{
    private readonly Lock gate = new();
    private readonly List<(WeakReference<object> Owner, Action<object> Refresh, Dispatcher Home)> tracked = [];

    private Loc()
    {
        Strings.Changed += OnChanged;
    }

    public static Loc Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Strings.Get(key);

    public static void Track(object owner, Action<object> refresh)
    {
        lock (Current.gate)
        {
            Current.tracked.Add((new WeakReference<object>(owner), refresh, Dispatcher.CurrentDispatcher));
        }
    }

    private void OnChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));

        List<(object Owner, Action<object> Refresh, Dispatcher Home)> alive = [];

        lock (gate)
        {
            tracked.RemoveAll(entry => !entry.Owner.TryGetTarget(out _) || !entry.Home.Thread.IsAlive || entry.Home.HasShutdownStarted);

            foreach (var (owner, refresh, home) in tracked)
            {
                if (owner.TryGetTarget(out var target))
                {
                    alive.Add((target, refresh, home));
                }
            }
        }

        foreach (var (owner, refresh, home) in alive)
        {
            if (home.CheckAccess())
            {
                refresh(owner);
            }
            else
            {
                home.BeginInvoke(() => refresh(owner));
            }
        }
    }
}
