using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Kebuz.Gui.Player;

public partial class FullScreenWindow : Window
{
    public static readonly DependencyProperty ChromeShownProperty = DependencyProperty.Register(
        nameof(ChromeShown),
        typeof(bool),
        typeof(FullScreenWindow),
        new PropertyMetadata(true, OnChromeShownChanged));

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer fade;

    private Point pointer;
    private bool closed;

    public FullScreenWindow()
    {
        InitializeComponent();

        Scrub.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnScrubPressed), true);
        Sound.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnScrubPressed), true);

        fade = new DispatcherTimer { Interval = Idle };
        fade.Tick += OnIdle;

        DataContextChanged += OnModelChanged;
        Closed += OnGone;
    }

    public bool ChromeShown
    {
        get => (bool)GetValue(ChromeShownProperty);
        set => SetValue(ChromeShownProperty, value);
    }

    private PlayerModel? Model => DataContext as PlayerModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        Cover();
        WindowState = WindowState.Maximized;
        Reveal();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        var spot = e.GetPosition(this);

        if ((spot - pointer).Length < 1)
        {
            return;
        }

        pointer = spot;
        Reveal();
    }

    private static void OnChromeShownChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var window = (FullScreenWindow)target;
        window.Cursor = (bool)e.NewValue ? Cursors.Arrow : Cursors.None;
    }

    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PlayerModel old)
        {
            old.PropertyChanged -= OnPlayingChanged;
        }

        if (e.NewValue is PlayerModel fresh)
        {
            fresh.PropertyChanged += OnPlayingChanged;
        }
    }

    private void OnPlayingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerModel.IsPlaying))
        {
            Reveal();
            return;
        }

        if (e.PropertyName == nameof(PlayerModel.HasClip) && Model is { HasClip: false })
        {
            Close();
        }
    }

    private void OnGone(object? sender, EventArgs e)
    {
        closed = true;
        fade.Stop();

        if (Model is { } model)
        {
            model.PropertyChanged -= OnPlayingChanged;
            model.EndTrim();
        }
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        fade.Stop();

        if (Model is { IsPlaying: true, Scrubbing: false, IsTrimming: false, Gallery.IsRemoving: false } && !TagPopup.IsOpen)
        {
            Conceal();
        }
    }

    private void Reveal()
    {
        if (closed)
        {
            return;
        }

        ChromeShown = true;
        fade.Stop();
        fade.Start();
    }

    private void Conceal()
    {
        fade.Stop();
        ChromeShown = false;
    }

    private void OnStageClicked(object sender, MouseButtonEventArgs e)
    {
        Model?.Toggle();
    }

    private void OnStageRightClicked(object sender, MouseButtonEventArgs e)
    {
        if (ChromeShown)
        {
            Conceal();
        }
        else
        {
            Reveal();
        }

        e.Handled = true;
    }

    private void OnChromeClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (Model is not { } model || e.Delta == 0)
        {
            return;
        }

        model.Nudge(Math.Sign(e.Delta));
        Reveal();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        Reveal();

        switch (e.Key)
        {
            case Key.Escape when Model is { Gallery.IsRemoving: true }:
                Model.Gallery.CancelRemove();
                e.Handled = true;
                break;

            case Key.Escape when Model is { IsSaveAsked: true }:
                Model.CancelSave();
                TrimSaveButton.Focus();
                e.Handled = true;
                break;

            case Key.Escape when Model is { IsTrimming: true }:
                Model.EndTrim();
                e.Handled = true;
                break;

            case Key.I when Model is { IsTrimming: true }:
                Model.MarkStart();
                e.Handled = true;
                break;

            case Key.O when Model is { IsTrimming: true }:
                Model.MarkEnd();
                e.Handled = true;
                break;

            case Key.Left when Model is { IsTrimming: true }:
                Model.StepFrame(-1);
                e.Handled = true;
                break;

            case Key.Right when Model is { IsTrimming: true }:
                Model.StepFrame(1);
                e.Handled = true;
                break;

            case Key.Escape when !TagPopup.IsOpen:
            case Key.F:
                Close();
                e.Handled = true;
                break;

            case Key.Space when e.OriginalSource is not System.Windows.Controls.TextBox:
                Model?.Toggle();
                e.Handled = true;
                break;

            case Key.Left:
                Model?.Step(-1);
                e.Handled = true;
                break;

            case Key.Right:
                Model?.Step(1);
                e.Handled = true;
                break;
        }
    }

    private void Cover()
    {
        var owner = Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
        var anchor = owner == IntPtr.Zero ? new WindowInteropHelper(this).Handle : owner;
        var monitor = PInvoke.MonitorFromWindow(new HWND(anchor), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        if (monitor.IsNull)
        {
            return;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var target = PresentationSource.FromVisual(this)?.CompositionTarget;

        if (target is null)
        {
            return;
        }

        var corner = target.TransformFromDevice.Transform(new Point(info.rcMonitor.left, info.rcMonitor.top));
        Left = corner.X;
        Top = corner.Y;
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
    {
        Model?.Step(-1);
    }

    private void OnStepNext(object sender, RoutedEventArgs e)
    {
        Model?.Step(1);
    }

    private void OnPlayToggled(object sender, RoutedEventArgs e)
    {
        Model?.Toggle();
    }

    private void OnLeaveFullScreen(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnScrubPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider
            || slider.Template.FindName("PART_Track", slider) is not Track track
            || track.Thumb is not { IsDragging: false } thumb)
        {
            return;
        }

        slider.UpdateLayout();

        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = MouseLeftButtonDownEvent,
            Source = thumb
        });
    }

    private void OnScrubStarted(object sender, DragStartedEventArgs e)
    {
        if (Model is { } model)
        {
            model.Scrubbing = true;
        }
    }

    private void OnScrubDone(object sender, DragCompletedEventArgs e)
    {
        if (Model is { } model)
        {
            model.Scrubbing = false;
        }
    }

    private void OnRemoveAsked(object sender, RoutedEventArgs e)
    {
        Model?.Gallery.AskRemove(true);
        Reveal();
    }

    private void OnRemoveCancel(object sender, RoutedEventArgs e)
    {
        Model?.Gallery.CancelRemove();
        Reveal();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        Model?.Gallery.Remove();
        Reveal();
    }

    private void OnTrimBegin(object sender, RoutedEventArgs e)
    {
        Model?.BeginTrim();
        Reveal();
    }

    private void OnTrimStart(object sender, RoutedEventArgs e)
    {
        Model?.MarkStart();
    }

    private void OnTrimEnd(object sender, RoutedEventArgs e)
    {
        Model?.MarkEnd();
    }

    private void OnTrimCancel(object sender, RoutedEventArgs e)
    {
        Model?.EndTrim();
    }

    private void OnTrimSave(object sender, RoutedEventArgs e)
    {
        Model?.AskSave();

        if (Model is { IsSaveAsked: true })
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TrimKeepButton.Focus());
        }
    }

    private void OnTrimKeep(object sender, RoutedEventArgs e)
    {
        Model?.SaveTrim(false);
    }

    private void OnTrimReplace(object sender, RoutedEventArgs e)
    {
        Model?.SaveTrim(true);
    }

    private void OnTrimBack(object sender, RoutedEventArgs e)
    {
        Model?.CancelSave();
        TrimSaveButton.Focus();
    }

    private void OnTagsOpen(object sender, RoutedEventArgs e)
    {
        TagPopup.IsOpen = true;
    }

    private void OnTagsOpened(object? sender, EventArgs e)
    {
        TagForm.TakeFocus();
    }

    private void OnTagsDismissed()
    {
        TagPopup.IsOpen = false;
        TagButton.Focus();
    }
}
