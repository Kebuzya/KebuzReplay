using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kebuz.Gui.Gallery;

public partial class RecordingPanel : UserControl
{
    public RecordingPanel() => InitializeComponent();
    private RecordingPanelModel? Model => DataContext as RecordingPanelModel;
    private void OnToggle(object sender, RoutedEventArgs e) => Model?.Toggle();
    private void OnSettings(object sender, RoutedEventArgs e) => Model?.ToggleSettings();
    private void OnSources(object? sender, EventArgs e) => Model?.RefreshSources();
    private void OnSaveStop(object sender, RoutedEventArgs e) => Model?.Stop(true);
    private void OnDiscard(object sender, RoutedEventArgs e) => Model?.Stop(false);
    private void OnCancel(object sender, RoutedEventArgs e) => Model?.CancelStop();
    private void OnRecover(object sender, RoutedEventArgs e) => Model?.Recover();
    private void OnClearHotkey(object sender, RoutedEventArgs e) => Model?.ClearHotkey();
    private void OnKey(object sender, KeyEventArgs e) => ControlKey.OnKey(e);
}
