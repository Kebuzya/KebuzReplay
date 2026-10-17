using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;
using Kebuz.Recorder.Watching;

namespace Kebuz.Gui.Settings;

public sealed class WatchedAppRow : INotifyPropertyChanged
{
    private bool confirming;

    public WatchedAppRow(WatchedApp app)
    {
        App = app;
        Loc.Track(this, static owner => ((WatchedAppRow)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WatchedApp App { get; }

    public string Title => App.Title;

    public string Executable => App.Executable;

    public string Location => App.Path ?? App.Executable;

    public bool IsConfirming
    {
        get => confirming;
        set
        {
            if (confirming == value)
            {
                return;
            }

            confirming = value;
            Raise();
        }
    }

    public string Warning => Strings.Format("Settings.Games.RemoveQuestion", Title);

    public bool HasProfile => App.Overrides.Any;

    public string ProfileText => App.Overrides.ToString();

    public string ProfileName => Strings.Format("Settings.Games.ProfileName", Title);

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    public override string ToString()
    {
        return $"{Title}, {Executable}";
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class FoundGameRow : INotifyPropertyChanged
{
    public FoundGameRow(DetectedGame game)
    {
        Game = game;
        Loc.Track(this, static owner => ((FoundGameRow)owner).Relocalize());
    }

    public DetectedGame Game { get; }

    public string Title => Game.Title;

    public string Executable => Game.Executable;

    public string Location => Game.Path;

    public string RecordName => Strings.Format("Settings.Games.RecordName", Title);

    public string RejectName => Strings.Format("Settings.Games.RejectName", Title);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public override string ToString()
    {
        return $"{Title}, {Executable}";
    }
}

public sealed class IgnoredAppRow : INotifyPropertyChanged
{
    public IgnoredAppRow(IgnoredApp app)
    {
        App = app;
        Loc.Track(this, static owner => ((IgnoredAppRow)owner).Relocalize());
    }

    public IgnoredApp App { get; }

    public string Title => App.Title;

    public string Executable => App.Executable;

    public string Location => App.Path ?? App.Executable;

    public string RemoveName => Strings.Format("Settings.Games.RemoveIgnored", Title);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public override string ToString()
    {
        return $"{Title}, {Executable}";
    }
}

public sealed class RunningAppRow : INotifyPropertyChanged
{
    public RunningAppRow(RunningApp app)
    {
        App = app;
        Loc.Track(this, static owner => ((RunningAppRow)owner).Relocalize());
    }

    public RunningApp App { get; }

    public string Title => App.Title;

    public string Executable => App.Executable;

    public string Location => App.Path;

    public string GameName => Strings.Format("Settings.Games.AddGame", Title);

    public string IgnoreName => Strings.Format("Settings.Games.AddIgnored", Title);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public override string ToString()
    {
        return $"{Title}, {Executable}";
    }
}

public sealed class WatchedAppsModel : INotifyPropertyChanged
{
    private string complaint = string.Empty;
    private bool picking;

    public WatchedAppsModel()
    {
        Reload();
        ReloadIgnored();
        Loc.Track(this, static owner => ((WatchedAppsModel)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<FoundGameRow>? Settled;

    public event Action? ProfileEdited;

    public ObservableCollection<WatchedAppRow> Apps { get; } = [];

    public ObservableCollection<FoundGameRow> Found { get; } = [];

    public ObservableCollection<IgnoredAppRow> Ignored { get; } = [];

    public ObservableCollection<RunningAppRow> Running { get; } = [];

    public bool IsEmpty => Apps.Count == 0;

    public bool HasFound => Found.Count > 0;

    public bool HasIgnored => Ignored.Count > 0;

    public bool IsPicking => picking;

    public bool HasNoRunning => picking && Running.Count == 0;

    public FoundGameRow? Detect(DetectedGame game)
    {
        foreach (var row in Found)
        {
            if (string.Equals(row.Location, game.Path, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        var added = new FoundGameRow(game);
        Found.Add(added);
        Raise(nameof(HasFound));

        return added;
    }

    public void Accept(FoundGameRow row)
    {
        Settle(row);
        Edit(apps => apps.Add(row.Game.Path, row.Title));
    }

    public void Reject(FoundGameRow row)
    {
        Settle(row);
        EditIgnored(ignored => ignored.Add(row.Game.Path));
    }

    public void Unignore(IgnoredAppRow row)
    {
        EditIgnored(ignored => ignored.Remove(row.App));
    }

    public void Pick()
    {
        Running.Clear();

        try
        {
            var watched = WatchedApps.Load();
            var ignored = IgnoredApps.Load();

            foreach (var app in RunningWindows.List())
            {
                if (watched.Find(app.Executable, app.Path) is null
                    && ignored.Items.All(item => !string.Equals(item.Path, app.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    Running.Add(new RunningAppRow(app));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("не удалось перечислить запущенные приложения", ex);
            Complain(Strings.Format("Settings.Games.ListRunningFailed", ex.Message));
        }

        SetPicking(true);
    }

    public void StopPicking()
    {
        Running.Clear();
        SetPicking(false);
    }

    public void AddRunning(RunningAppRow row)
    {
        Take(row);
        Edit(apps => apps.Add(row.App.Path, WatchedApps.TitleOf(row.App.Path)));
    }

    public void IgnoreRunning(RunningAppRow row)
    {
        Take(row);
        EditIgnored(ignored => ignored.Add(row.App.Path));
    }

    public bool HasComplaint => complaint.Length > 0;

    public string Complaint => complaint;

    public void Add(string target)
    {
        Edit(apps => apps.Add(target));
    }

    public void Ask(WatchedAppRow row)
    {
        foreach (var other in Apps)
        {
            other.IsConfirming = ReferenceEquals(other, row);
        }
    }

    public void Cancel()
    {
        foreach (var row in Apps)
        {
            row.IsConfirming = false;
        }
    }

    public void Remove(WatchedAppRow row)
    {
        Edit(apps => apps.Remove(row.App));
    }

    public void SetProfile(WatchedAppRow row, AppProfile profile)
    {
        Edit(apps => apps.SetProfile(row.App, profile));
        ProfileEdited?.Invoke();
    }

    private void Settle(FoundGameRow row)
    {
        if (Found.Remove(row))
        {
            Raise(nameof(HasFound));
        }

        Settled?.Invoke(row);
    }

    private void Take(RunningAppRow row)
    {
        Running.Remove(row);
        Raise(nameof(HasNoRunning));
    }

    private void SetPicking(bool next)
    {
        picking = next;
        Raise(nameof(IsPicking));
        Raise(nameof(HasNoRunning));
    }

    private void EditIgnored(Action<IgnoredApps> change)
    {
        try
        {
            var ignored = IgnoredApps.Load();

            change(ignored);
            ignored.Save();

            Complain(string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось записать {IgnoredApps.DefaultPath}", ex);
            Complain(Strings.Format("Settings.Games.IgnoredSaveFailed", ex.Message));
        }

        ReloadIgnored();
    }

    private void ReloadIgnored()
    {
        Ignored.Clear();

        try
        {
            foreach (var app in IgnoredApps.Load().Items)
            {
                Ignored.Add(new IgnoredAppRow(app));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось прочитать {IgnoredApps.DefaultPath}", ex);
            Complain(Strings.Format("Settings.Games.IgnoredReadFailed", ex.Message));
        }

        Raise(nameof(HasIgnored));
        Prune();
    }

    private void Edit(Action<WatchedApps> change)
    {
        try
        {
            var apps = WatchedApps.Load();

            change(apps);
            apps.Save();

            Complain(string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось записать {WatchedApps.DefaultPath}", ex);
            Complain(Strings.Format("Settings.Games.SaveFailed", ex.Message));
        }

        Reload();
    }

    private void Reload()
    {
        Apps.Clear();

        try
        {
            foreach (var app in WatchedApps.Load().Items)
            {
                Apps.Add(new WatchedAppRow(app));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось прочитать {WatchedApps.DefaultPath}", ex);
            Complain(Strings.Format("Settings.Games.ReadFailed", ex.Message));
        }

        Raise(nameof(IsEmpty));
        Prune();
    }

    private void Prune()
    {
        WatchedApps watched;
        IgnoredApps ignored;

        try
        {
            watched = WatchedApps.Load();
            ignored = IgnoredApps.Load();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var row in Found.ToList())
        {
            if (watched.Match(row.Executable, () => row.Location) is not null
                || ignored.Contains(row.Executable, row.Location))
            {
                Settle(row);
            }
        }

        foreach (var row in Running.ToList())
        {
            if (watched.Find(row.Executable, row.Location) is not null
                || ignored.Contains(row.Executable, row.Location))
            {
                Take(row);
            }
        }
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Complain(string text)
    {
        if (complaint == text)
        {
            return;
        }

        complaint = text;
        Raise(nameof(HasComplaint));
        Raise(nameof(Complaint));
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
