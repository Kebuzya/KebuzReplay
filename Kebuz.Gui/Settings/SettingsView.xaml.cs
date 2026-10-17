using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Settings;

public partial class SettingsView : UserControl
{
    private const double SectionReach = 48;

    private bool steering;

    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnApplyNow(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsModel)?.ApplyNow();
    }

    private void OnCloseApplyNotice(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsModel)?.CloseApplyNotice();
    }

    private void OnAddGame(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsModel model)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("Settings.Games.PickTitle"),
            Filter = Strings.Get("Settings.Common.AppFilter"),
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            model.Games.Add(dialog.FileName);
        }
    }

    private void OnRemoveGameAsked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: WatchedAppRow row })
        {
            model.Games.Ask(row);
        }
    }

    private void OnRemoveGameCancel(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsModel)?.Games.Cancel();
    }

    private void OnRemoveGame(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: WatchedAppRow row })
        {
            model.Games.Remove(row);
        }
    }

    private void OnEditProfile(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: WatchedAppRow row })
        {
            model.Games.Cancel();
            model.OpenProfile(row);
        }
    }

    private void OnPickRunning(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsModel)?.Games.Pick();
    }

    private void OnPickDone(object sender, RoutedEventArgs e)
    {
        (DataContext as SettingsModel)?.Games.StopPicking();
    }

    private void OnRunningAdded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: RunningAppRow row })
        {
            model.Games.AddRunning(row);
        }
    }

    private void OnRunningIgnored(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: RunningAppRow row })
        {
            model.Games.IgnoreRunning(row);
        }
    }

    private void OnFoundAccepted(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: FoundGameRow row })
        {
            model.Games.Accept(row);
        }
    }

    private void OnFoundRejected(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: FoundGameRow row })
        {
            model.Games.Reject(row);
        }
    }

    private void OnUnignore(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsModel model && sender is Button { Tag: IgnoredAppRow row })
        {
            model.Games.Unignore(row);
        }
    }

    private void OnSectionPicked(object sender, RoutedEventArgs e)
    {
        if (steering || sender is not RadioButton { Tag: FrameworkElement section })
        {
            return;
        }

        steering = true;
        section.BringIntoView();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => steering = false);
    }

    private void OnBodyScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (steering || e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
        {
            return;
        }

        RadioButton? current = null;
        var items = Nav.Children.OfType<RadioButton>().ToList();

        foreach (var item in items)
        {
            if (item.Tag is not FrameworkElement section)
            {
                continue;
            }

            if (section.TranslatePoint(new Point(0, 0), Body).Y <= SectionReach)
            {
                current = item;
            }
        }

        if (Body.ScrollableHeight > 0 && Body.VerticalOffset >= Body.ScrollableHeight - 1)
        {
            current = items.LastOrDefault(item => item.Tag is FrameworkElement) ?? current;
        }

        current ??= items.FirstOrDefault();

        if (current is null || current.IsChecked == true)
        {
            return;
        }

        steering = true;
        current.IsChecked = true;
        steering = false;
    }

    private void OnKeyCaptured(object sender, KeyEventArgs e)
    {
        HotkeyPart.OnKey(e);
    }
}
