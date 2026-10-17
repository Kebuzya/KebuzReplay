using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Settings;

public partial class HelpButton : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(HelpButton), new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpButton), new PropertyMetadata(string.Empty));

    public HelpButton()
    {
        InitializeComponent();
        PreviewKeyDown += OnPanelKey;
        Sheet.PreviewKeyDown += OnPanelKey;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsOpen
    {
        get => Toggle.IsChecked == true;
        set => Toggle.IsChecked = value;
    }

    private static void OnTitleChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var help = (HelpButton)owner;
        var title = e.NewValue as string ?? string.Empty;

        System.Windows.Automation.AutomationProperties.SetName(help.Toggle, Strings.Format("Settings.Help.Name", title));
        var key = System.Windows.Data.BindingOperations.GetBinding(help, TitleProperty)?.Path?.Path?.Trim('[', ']');
        var id = string.IsNullOrEmpty(key) ? title : key.Replace(".", string.Empty);

        System.Windows.Automation.AutomationProperties.SetAutomationId(help.Toggle, $"Help{id}");
    }

    private void OnPanelKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Toggle.IsChecked != true)
        {
            return;
        }

        Toggle.IsChecked = false;
        Toggle.Focus();
        e.Handled = true;
    }
}
