using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kebuz.Gui.Settings;

public partial class HotkeySection : UserControl
{
    public HotkeySection()
    {
        InitializeComponent();
    }

    public void OnKey(KeyEventArgs e)
    {
        if (DataContext is not HotkeyModel model || !model.Editing)
        {
            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            model.Toggle();
            return;
        }

        var modifiers = Keyboard.Modifiers;

        model.Capture(
            KeyInterop.VirtualKeyFromKey(key),
            modifiers.HasFlag(ModifierKeys.Control),
            modifiers.HasFlag(ModifierKeys.Alt),
            modifiers.HasFlag(ModifierKeys.Shift),
            modifiers.HasFlag(ModifierKeys.Windows));
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        (DataContext as HotkeyModel)?.Toggle();
    }
}
