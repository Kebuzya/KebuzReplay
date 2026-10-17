using System.Windows.Data;
using System.Windows.Markup;

namespace Kebuz.Gui.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class T : MarkupExtension
{
    public T()
    {
    }

    public T(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Current,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
