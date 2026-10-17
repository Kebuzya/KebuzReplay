using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kebuz.Gui.Settings;

public partial class QualitySection : SectionControl
{
    private SampleModel? sample;
    private bool drawing;

    public QualitySection()
    {
        InitializeComponent();
        DataContextChanged += OnModelChanged;
        Unloaded += (_, _) => Sleep();
    }

    private void OnModelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sample is not null)
        {
            sample.Redraw -= Wake;
        }

        sample = (e.NewValue as QualityModel)?.Sample;

        if (sample is not null)
        {
            sample.Redraw += Wake;
        }
    }

    private void OnSample(object sender, RoutedEventArgs e)
    {
        if (DataContext is not QualityModel { Sample: { } model } quality)
        {
            return;
        }

        var window = Window.GetWindow(this);
        var near = window is null ? 0 : new WindowInteropHelper(window).Handle;

        model.Start(quality.Effective, near);
    }

    private void OnSampleClose(object sender, RoutedEventArgs e)
    {
        if (sample is not null)
        {
            sample.IsOpen = false;
        }
    }

    private void Wake()
    {
        if (drawing)
        {
            return;
        }

        drawing = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Sleep()
    {
        if (!drawing)
        {
            return;
        }

        drawing = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (sample is null || !sample.Draw())
        {
            Sleep();
        }
    }
}
