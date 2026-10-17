using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Kebuz.Gui.Gallery;

public partial class TagPopover : UserControl
{
    public TagPopover()
    {
        InitializeComponent();
    }

    public event Action? Dismissed;

    public void TakeFocus()
    {
        TagName.Focus();
    }

    private GalleryModel? Model => DataContext as GalleryModel;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        Dismissed?.Invoke();
        e.Handled = true;
    }

    private void OnTagToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not TagChoice choice)
        {
            return;
        }

        Model?.Toggle(choice);
        box.SetCurrentValue(ToggleButton.IsCheckedProperty, choice.State);
    }

    private void OnTagRemoveAsked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChoice choice })
        {
            Model?.AskDelete(choice);
        }
    }

    private void OnTagRemoveCancel(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChoice choice })
        {
            choice.IsConfirming = false;
        }
    }

    private void OnTagRemove(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChoice choice })
        {
            Model?.Delete(choice);
        }
    }

    private void OnTagEdit(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChoice choice })
        {
            Model?.StartEdit(choice);
            TagName.Focus();
            TagName.SelectAll();
        }
    }

    private void OnTagEditCancel(object sender, RoutedEventArgs e)
    {
        Model?.CancelEdit();
        TagName.Focus();
    }

    private void OnTagCommit(object sender, RoutedEventArgs e)
    {
        Model?.Commit();
        TagName.Focus();
    }
}
