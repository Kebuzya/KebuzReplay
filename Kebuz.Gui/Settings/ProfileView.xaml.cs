using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Kebuz.Gui.Settings;

public partial class ProfileView : UserControl
{
    private bool steering;

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
        if (steering || sender is not RadioButton { Tag: Border section })
        {
            return;
        }

        Navigate(section, IsLoaded);
    }

    private void OnSectionClicked(object sender, RoutedEventArgs e)
    {
        if (steering || sender is not RadioButton { Tag: Border section })
        {
            return;
        }

        Navigate(section, true);
    }

    private void Navigate(Border section, bool pulse)
    {
        steering = true;
        section.BringIntoView();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            steering = false;

            if (pulse)
            {
                SectionPulse.Show(section);
            }
        });
    }

    private void OnKeyCaptured(object sender, KeyEventArgs e)
    {
        HotkeyBlock.OnKey(e);
    }
}
