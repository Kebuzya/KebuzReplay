using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Kebuz.Gui.Player;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Gallery;

public partial class GalleryView : UserControl
{
    private PlayerModel? player;
    private FullScreenWindow? theatre;
    private bool drawing;

    public GalleryView()
    {
        InitializeComponent();

        Scrub.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnScrubPressed), true);
        Sound.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnScrubPressed), true);

        DataContextChanged += OnModelChanged;
        Loaded += OnViewLoaded;
        IsVisibleChanged += OnViewVisibility;
    }

    public PlayerModel? Player => player;

    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not GalleryModel model)
        {
            return;
        }

        player?.Dispose();

        var fresh = new PlayerModel(model);
        fresh.Redraw += Wake;
        player = fresh;

        PlayerPanel.DataContext = fresh;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        player?.Attach();
    }

    private void OnViewVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (player is null)
        {
            return;
        }

        if (IsVisible)
        {
            player.Resume();
        }
        else if (theatre is null)
        {
            player.Suspend();
        }
    }

    private void Wake()
    {
        if (drawing || player is null)
        {
            return;
        }

        drawing = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Sleep()
    {
        if (!drawing)
        {
            return;
        }

        drawing = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (player is null || !player.Draw())
        {
            Sleep();
        }
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
    {
        player?.Step(-1);
    }

    private void OnStepNext(object sender, RoutedEventArgs e)
    {
        player?.Step(1);
    }

    private void OnPlayToggled(object sender, RoutedEventArgs e)
    {
        player?.Toggle();
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
        if (player is not null)
        {
            player.Scrubbing = true;
        }
    }

    private void OnScrubDone(object sender, DragCompletedEventArgs e)
    {
        if (player is not null)
        {
            player.Scrubbing = false;
        }
    }

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (player is null || e.Delta == 0)
        {
            return;
        }

        player.Nudge(Math.Sign(e.Delta));
        e.Handled = true;
    }

    private void OnFullScreen(object sender, RoutedEventArgs e)
    {
        if (player is null || theatre is not null)
        {
            return;
        }

        var window = new FullScreenWindow
        {
            Owner = Window.GetWindow(this),
            DataContext = player
        };

        window.Closed += (_, _) =>
        {
            theatre = null;
            player.IsFullScreen = false;
        };

        theatre = window;
        player.IsFullScreen = true;
        window.Show();
    }

    private void OnGameFolded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameGroup group } && DataContext is GalleryModel model)
        {
            model.Fold(group);
        }
    }

    private void OnClipPicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.DataContext is not ClipItem clip)
        {
            return;
        }

        if (DataContext is GalleryModel model)
        {
            var extend = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var range = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            model.Choose(clip, extend, range);
        }

        button.SetCurrentValue(ToggleButton.IsCheckedProperty, clip.IsChosen);
    }

    private void OnApplySettings(object sender, RoutedEventArgs e)
    {
        (DataContext as GalleryModel)?.RequestApply();
    }

    private void OnMarkClip(object sender, RoutedEventArgs e)
    {
        (DataContext as GalleryModel)?.RequestMark();
    }

    private void OnRemoveAsked(object sender, RoutedEventArgs e)
    {
        (DataContext as GalleryModel)?.AskRemove();
    }

    private void OnRemoveCancel(object sender, RoutedEventArgs e)
    {
        (DataContext as GalleryModel)?.CancelRemove();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        (DataContext as GalleryModel)?.Remove();
    }

    private void OnRevealFolder(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GalleryModel model || model.Selected is not { } clip)
        {
            return;
        }

        if (!ShellFiles.Reveal(clip.Path))
        {
            model.NoteTrouble(Strings.Get("Gallery.FolderFailed"));
        }
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
