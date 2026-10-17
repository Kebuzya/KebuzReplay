using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Gui.Shell;

public partial class UpdateBar : UserControl
{
    public UpdateBar()
    {
        InitializeComponent();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UpdateModel model)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(model.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"страница версии не открылась: {model.Url}", ex);
        }
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        (DataContext as UpdateModel)?.Dismiss();
    }
}
