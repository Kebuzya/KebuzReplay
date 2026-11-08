using System.Windows;
using System.Windows.Controls;
using Kebuz.Gui.Settings;

namespace Kebuz.Gui.Gallery;

public partial class GameCandidatePanel : UserControl
{
    public GameCandidatePanel()
    {
        InitializeComponent();
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (DataContext is WatchedAppsModel { CurrentFound: { } row } model)
        {
            model.Accept(row);
        }
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        if (DataContext is WatchedAppsModel { CurrentFound: { } row } model)
        {
            model.Reject(row);
        }
    }
}
