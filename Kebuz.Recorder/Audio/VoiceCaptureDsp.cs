using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Kebuz.Recorder.Audio;

internal static class VoiceCaptureDsp
{
    public static readonly Guid Clsid = new("745057C7-F353-4F2D-A7EE-58434477730E");

    private static readonly Guid Keys = new("6F52C567-0360-4BD2-9617-CCBF1421C939");

    public static PROPERTYKEY SystemMode => Key(2);

    public static PROPERTYKEY SourceMode => Key(3);

    public static PROPERTYKEY FeatureMode => Key(5);

    public static PROPERTYKEY NoiseSuppression => Key(8);

    public static PROPERTYKEY GainControl => Key(9);

    public static IMFTransform Create()
    {
        PInvoke.CoCreateInstance<IMFTransform>(
            Clsid,
            null,
            CLSCTX.CLSCTX_INPROC_SERVER,
            out var transform).ThrowOnFailure();

        return transform;
    }

    public static unsafe void SetInt(IPropertyStore store, PROPERTYKEY key, int value)
    {
        var variant = new PROPVARIANT();
        variant.vt = VARENUM.VT_I4;
        variant.lVal = value;
        store.SetValue(&key, in variant);
    }

    public static unsafe void SetBool(IPropertyStore store, PROPERTYKEY key, bool value)
    {
        var variant = new PROPVARIANT();
        variant.vt = VARENUM.VT_BOOL;
        variant.boolVal = value;
        store.SetValue(&key, in variant);
    }

    public static unsafe string Read(IPropertyStore store, PROPERTYKEY key)
    {
        try
        {
            store.GetValue(&key, out var variant);

            return variant.vt switch
            {
                VARENUM.VT_I4 => $"I4 {variant.lVal}",
                VARENUM.VT_BOOL => $"BOOL {variant.boolVal.Value}",
                VARENUM.VT_EMPTY => "пусто",
                _ => $"тип {variant.vt}"
            };
        }
        catch (COMException ex)
        {
            return $"ошибка 0x{ex.HResult:X8}";
        }
    }

    private static PROPERTYKEY Key(uint id)
    {
        return new PROPERTYKEY { fmtid = Keys, pid = id };
    }
}
