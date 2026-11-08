using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Settings;

public partial class SoundSection : SectionControl
{
    public SoundSection()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
    }

    private void OnDevicesOpened(object sender, EventArgs e)
    {
        (DataContext as SoundModel)?.Refresh();
    }

    private void OnAddApp(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SoundModel model)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Strings.Get("Settings.Sound.PickAppTitle"),
            Filter = Strings.Get("Settings.Common.AppFilter"),
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            model.Add(Path.GetFileName(dialog.FileName));
        }
    }

    private void OnRemoveApp(object sender, RoutedEventArgs e)
    {
        if (DataContext is SoundModel model && sender is Button { Tag: AudioAppRow row })
        {
            model.Remove(row);
        }
    }

    private async void OnCaptureTest(object sender, RoutedEventArgs e)
    {
        if (DataContext is SoundModel model)
        {
            await model.CaptureTest();
        }
    }

    private void OnPlayTest(object sender, RoutedEventArgs e)
    {
        (DataContext as SoundModel)?.ToggleTestPlayback();
    }

    private void OnTestSolo(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioTestSourceRow source })
        {
            source.Pick();
        }
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            (DataContext as SoundModel)?.DeactivateTest();
        }
    }
}
