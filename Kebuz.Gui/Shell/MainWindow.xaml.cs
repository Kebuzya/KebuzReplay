using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Kebuz.Gui.Gallery;
using Kebuz.Gui.Localization;
using Kebuz.Gui.Settings;
using Kebuz.Gui.Shell;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui;

public partial class MainWindow : Window
{
    private static readonly Geometry MaximizeGeometry = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
    private static readonly TimeSpan ShotFresh = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);
    private static readonly Geometry RestoreGeometry = Geometry.Parse("M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

    private readonly AppLink? link;
    private readonly SettingsModel settings;
    private readonly GalleryModel gallery;
    private readonly UpdateModel updates = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = UpdateInterval };

    private TrayIcon? tray;
    private ToastWindow? toast;
    private DetectToastWindow? question;
    private bool exiting;
    private bool minimized;
    private string? loadedRoot;
    private bool levelsWatched;
    private bool updateChecking;
    private bool updateChecked;

    public MainWindow(AppLink? link)
    {
        this.link = link;

        InitializeComponent();
        Loc.Track(this, static owner => ((MainWindow)owner).Relocalize());
        StateChanged += OnStateChanged;

        settings = new SettingsModel(link);
        gallery = new GalleryModel();

        SettingsScreen.DataContext = settings;
        GalleryScreen.DataContext = gallery;
        UpdatePart.DataContext = updates;

        updates.Dismissed += settings.SkipVersion;
        updateTimer.Tick += (_, _) => CheckUpdates();

        gallery.StorageChanged += () =>
            settings.Occupied(gallery.OccupiedBytes, gallery.ClipCount, gallery.TaggedBytes);
        gallery.MarkRequested += OnMarkRequested;
        gallery.ApplyRequested += settings.ApplyNow;
        gallery.ClipArrived += OnClipArrived;
        settings.Pushed += OnPushed;
        settings.RereadRequested += gallery.Reread;
        settings.Games.Settled += OnGameSettled;

        if (link is null)
        {
            Adopt(KebuzSettings.Default);
            return;
        }

        link.RecorderRestarted += reason => Dispatcher.Invoke(() => gallery.NoteRestart(reason));
        link.CaptureRecovered += reason => Dispatcher.Invoke(() => gallery.NoteRecovery(reason));
        link.ClipSaved += entry => Dispatcher.Invoke(() => gallery.Add(entry));
        link.RecordingStateChanged += state => Dispatcher.Invoke(() =>
        {
            gallery.SetRecording(state);
            settings.SetRecording(state);
        });
        link.BufferMemoryUsage += bytes => Dispatcher.Invoke(() => gallery.SetMemory(bytes));
        link.AudioSourceChanged += health => Dispatcher.Invoke(() => OnAudioSourceChanged(health));
        link.AudioLevels += levels => Dispatcher.BeginInvoke(() => OnAudioLevels(levels));
        link.GameDetected += game => Dispatcher.Invoke(() => OnGameDetected(game));
        link.CaptureShown += shot => Dispatcher.Invoke(() => OnCaptureShown(shot));
        link.ShowRequested += () => Dispatcher.Invoke(ShowFromTray);
        SettingsScreen.IsVisibleChanged += (_, _) => WatchLevels();
        link.WhenSettings(next => Dispatcher.Invoke(() => Adopt(next)));
        link.WhenHotkey(state => Dispatcher.Invoke(() => OnHotkeyChanged(state)));
    }

    private void Adopt(KebuzSettings next)
    {
        settings.Adopt(next);
        OnPushed(next);
    }

    private void OnPushed(KebuzSettings next)
    {
        Strings.Use(Strings.Resolve(next.UiLanguage));
        gallery.Adopt(next);
        updates.Adopt(next.CheckUpdates, next.SkippedVersion);

        if (link is not null && next.CheckUpdates && !updateChecked)
        {
            CheckUpdates();
            updateTimer.Start();
        }

        if (loadedRoot == next.EffectiveClipsRoot)
        {
            return;
        }

        loadedRoot = next.EffectiveClipsRoot;
        gallery.Load(loadedRoot);
    }

    private async void CheckUpdates()
    {
        if (updateChecking || !settings.CheckUpdates)
        {
            return;
        }

        updateChecking = true;
        updateChecked = true;

        try
        {
            var found = await UpdateCheck.Latest();
            updates.Found(found);
            Log.Report(found is null
                ? $"проверка версии: релизов на GitHub нет, у нас {UpdateCheck.CurrentText}"
                : $"проверка версии: на GitHub {found.Tag}, у нас {UpdateCheck.CurrentText}");
        }
        catch (Exception ex)
        {
            Log.Report($"проверка версии не удалась: {ex.Message}");
        }
        finally
        {
            updateChecking = false;
        }
    }

    private void OnAudioSourceChanged(SourceHealth health)
    {
        gallery.NoteSound(health);

        if (health.Kind is "mic")
        {
            settings.Sound.Note(health.Live ? null : health.Complaint);
        }
    }

    private void OnAudioLevels(AudioLevels levels)
    {
        if (!levelsWatched)
        {
            return;
        }

        settings.Sound.Meter(levels);
        settings.Profile?.Sound.Meter(levels);
    }

    private void WatchLevels()
    {
        var wanted = link is not null && SettingsScreen.IsVisible && WindowState != WindowState.Minimized;

        if (wanted == levelsWatched)
        {
            return;
        }

        levelsWatched = wanted;
        link?.Send(PipeMessages.WatchAudioLevels, new LevelWatch(wanted));

        if (!wanted)
        {
            var silence = new AudioLevels([], 0, 0, false, false);
            settings.Sound.Meter(silence);
            settings.Profile?.Sound.Meter(silence);
        }
    }

    private void OnHotkeyChanged(HotkeyState state)
    {
        gallery.NoteHotkey(state);
        settings.Hotkey.Note(state);
    }

    private void OnMarkRequested()
    {
        if (link is null)
        {
            Log.Error("труба супервизора недоступна, метка не отправлена");
            return;
        }

        link.Send(PipeMessages.MarkClip);
    }

    private void OnClipArrived(ClipItem clip)
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            return;
        }

        Toast().Announce(clip);
    }

    private void OnCaptureShown(CaptureShot shot)
    {
        var card = CaptureCard.From(shot);
        gallery.SetCapture(card);

        if (IsActive || DateTime.UtcNow - card.Taken > ShotFresh)
        {
            return;
        }

        if (FullScreenGame.Exclusive())
        {
            Log.Report($"кадр окна не показан: эксклюзивный полноэкранный режим, {card.Notice} · {card.Window}");
            return;
        }

        Toast().Announce(card);
    }

    private ToastWindow Toast()
    {
        if (toast is null)
        {
            toast = new ToastWindow();
            toast.Picked += OnToastPicked;
            toast.Opened += OnToastOpened;
        }

        return toast;
    }

    private void OnToastOpened()
    {
        toast?.Dismiss();
        ShowFromTray();
        GalleryTab.IsChecked = true;
    }

    private void OnGameDetected(DetectedGame game)
    {
        if (settings.Games.Detect(game) is not { } row)
        {
            return;
        }

        if (question is null)
        {
            question = new DetectToastWindow();
            question.Answered += OnGameAnswered;
        }

        question.Ask(row);
    }

    private void OnGameAnswered(FoundGameRow row, bool record)
    {
        if (record)
        {
            settings.Games.Accept(row);
        }
        else
        {
            settings.Games.Reject(row);
        }
    }

    private void OnGameSettled(FoundGameRow row)
    {
        if (question?.Asking == row)
        {
            question.Dismiss();
        }
    }

    private void OnToastPicked(ClipItem clip)
    {
        toast?.Dismiss();
        ShowFromTray();
        GalleryTab.IsChecked = true;
        gallery.Bring(clip);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptionChrome.Attach(this, MaximizeButton);
        AttachTray();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (exiting)
        {
            return;
        }

        e.Cancel = true;
        GalleryScreen.Player?.Suspend();
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        toast?.Close();
        question?.Close();
        tray?.Dispose();
        gallery.Dispose();
        base.OnClosed(e);
    }

    private void AttachTray()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);

        if (source is null)
        {
            return;
        }

        var icon = new TrayIcon(handle, "KebuzReplay");
        icon.Opened += ShowFromTray;
        icon.MenuRequested += ShowTrayMenu;
        tray = icon;

        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            icon.Handle(message, lParam, ref handled));
    }

    private void ShowFromTray()
    {
        var hidden = !IsVisible;

        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        tray?.Foreground();
        GalleryScreen.Player?.Resume();

        if (hidden)
        {
            gallery.Reread();
        }
    }

    private void ShowTrayMenu()
    {
        if (tray is null || !tray.TryCursorPosition(out var x, out var y))
        {
            return;
        }

        var target = PresentationSource.FromVisual(this)?.CompositionTarget;

        if (target is null)
        {
            return;
        }

        var point = target.TransformFromDevice.Transform(new Point(x, y));

        tray.Foreground();

        var menu = (ContextMenu)FindResource("TrayContextMenu");
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = point.X;
        menu.VerticalOffset = point.Y;
        menu.IsOpen = true;
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
    }

    private void OnTrayExit(object sender, RoutedEventArgs e)
    {
        exiting = true;

        if (link is null)
        {
            Log.Error("труба супервизора недоступна, закрываю только окно");
        }
        else
        {
            link.Send(PipeMessages.Shutdown);
        }

        Application.Current.Shutdown();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            GalleryScreen.Player?.Suspend();
        }
        else
        {
            GalleryScreen.Player?.Resume();

            if (minimized && IsVisible)
            {
                gallery.Reread();
            }
        }

        minimized = WindowState == WindowState.Minimized;

        WatchLevels();

        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = maximized ? RestoreGeometry : MaximizeGeometry;
        ApplyMaximizeTitle(maximized);
    }

    private void Relocalize()
    {
        ApplyMaximizeTitle(WindowState == WindowState.Maximized);
    }

    private void ApplyMaximizeTitle(bool maximized)
    {
        var title = Strings.Get(maximized ? "Shell.Restore" : "Shell.Maximize");
        MaximizeButton.ToolTip = title;
        AutomationProperties.SetName(MaximizeButton, title);
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        CaptionChrome.Toggle(this);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
