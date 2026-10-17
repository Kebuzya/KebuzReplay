using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Kebuz.Gui.Settings;

public interface ISection
{
    void Commit();
}

public class SectionControl : UserControl
{
    private bool dragging;

    protected SectionControl()
    {
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
        AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
    }

    protected void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!dragging)
        {
            Commit();
        }
    }

    private void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        dragging = true;
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        dragging = false;
        Commit();
    }

    private void Commit()
    {
        (DataContext as ISection)?.Commit();
    }
}
