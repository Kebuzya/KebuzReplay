using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui;

public static class DistributionLicenseDialog
{
    public static bool Show(byte[] sdk, byte[]? runtime)
    {
        var window = new Window
        {
            Title = Strings.Get("License.Title"),
            Width = 660,
            Height = 510,
            MinWidth = 370,
            MinHeight = 320,
            MaxWidth = SystemParameters.WorkArea.Width,
            MaxHeight = SystemParameters.WorkArea.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        var body = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        var instruction = new TextBlock
        {
            Text = Strings.Get("License.Instruction"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(instruction, Dock.Top);
        body.Children.Add(instruction);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        body.Children.Add(actions);

        var accept = new Button { Content = Strings.Get("License.Accept"), Padding = new Thickness(12, 4, 12, 4) };
        accept.Click += (_, _) => window.DialogResult = true;
        actions.Children.Add(accept);

        var cancel = new Button
        {
            Content = Strings.Get("License.Decline"),
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = accept.Padding,
        };
        actions.Children.Add(cancel);

        var sdkTerms = new FlowDocument();
        using (var stream = new MemoryStream(sdk, writable: false))
        {
            new TextRange(sdkTerms.ContentStart, sdkTerms.ContentEnd).Load(stream, DataFormats.Rtf);
        }

        var tabs = new TabControl();
        tabs.Items.Add(Tab("License.WindowsSdk", sdkTerms));
        if (runtime is not null)
        {
            var runtimeTerms = new FlowDocument(new Paragraph(new Run(Encoding.UTF8.GetString(runtime))));
            tabs.Items.Add(Tab("License.DotNet", runtimeTerms));
        }

        body.Children.Add(tabs);
        window.Content = body;
        window.Loaded += (_, _) => cancel.Focus();
        return window.ShowDialog() == true;
    }

    private static TabItem Tab(string title, FlowDocument terms) => new()
    {
        Header = Strings.Get(title),
        Content = new RichTextBox(terms)
        {
            IsReadOnly = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
        },
    };
}
