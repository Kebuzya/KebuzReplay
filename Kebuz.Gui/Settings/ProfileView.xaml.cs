using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kebuz.Gui.Settings;

public partial class ProfileView : UserControl
{
    public ProfileView()
    {
        InitializeComponent();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        (DataContext as ProfileModel)?.Close();
    }

    private void OnSectionPicked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: FrameworkElement section })
        {
            section.BringIntoView();
        }
    }

    private void OnKeyCaptured(object sender, KeyEventArgs e)
    {
        HotkeyBlock.OnKey(e);
    }
}
