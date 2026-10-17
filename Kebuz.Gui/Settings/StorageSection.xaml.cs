using System.Windows;
using Microsoft.Win32;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Settings;

public partial class StorageSection : SectionControl
{
    public StorageSection()
    {
        InitializeComponent();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsModel model)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = Strings.Get("Settings.Storage.Folder"),
            InitialDirectory = model.ClipsFolder,
            Multiselect = false
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            model.Relocate(dialog.FolderName);
        }
    }
}
