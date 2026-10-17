using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal sealed class MediaTransform : IDisposable
{
    private readonly IMFActivate activate;

    private bool disposed;

    private MediaTransform(IMFActivate activate, IMFTransform transform, string name)
    {
        this.activate = activate;
        Transform = transform;
        Name = name;
    }

    public IMFTransform Transform { get; }

    public string Name { get; }

    public static MediaTransform Open(
        Guid category,
        MFT_ENUM_FLAG flags,
        Guid majorType,
        Guid subtype,
        string what)
    {
        var candidates = Enumerate(category, flags, majorType, subtype);
        MediaTransform? opened = null;
        var rejected = new List<string>();

        foreach (var candidate in candidates)
        {
            if (opened is not null)
            {
                Marshal.ReleaseComObject(candidate);
                continue;
            }

            var name = FriendlyName(candidate);

            try
            {
                var transform = (IMFTransform)candidate.ActivateObject(typeof(IMFTransform).GUID);
                opened = new MediaTransform(candidate, transform, name);
            }
            catch (Exception ex)
            {
                rejected.Add($"{name} (0x{ex.HResult:X8})");
                Marshal.ReleaseComObject(candidate);
            }
        }

        if (opened is not null)
        {
            return opened;
        }

        var reason = rejected.Count > 0
            ? $"не активировался ни один из найденных: {string.Join(", ", rejected)}"
            : "MFTEnumEx не вернул ни одного";

        throw new InvalidOperationException($"{what} недоступен, {reason}");
    }

    public static unsafe IMFActivate[] Enumerate(
        Guid category,
        MFT_ENUM_FLAG flags,
        Guid majorType,
        Guid subtype)
    {
        var output = new MFT_REGISTER_TYPE_INFO
        {
            guidMajorType = majorType,
            guidSubtype = subtype
        };

        PInvoke.MFTEnumEx(
            category,
            flags,
            (MFT_REGISTER_TYPE_INFO?)null,
            output,
            out var raw,
            out var count).ThrowOnFailure();

        var result = new IMFActivate[count];

        try
        {
            for (var i = 0; i < count; i++)
            {
                var pointer = (nint)raw[i];
                result[i] = (IMFActivate)Marshal.GetObjectForIUnknown(pointer);
                Marshal.Release(pointer);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)raw);
        }

        return result;
    }

    public static string FriendlyName(IMFActivate activate)
    {
        return ((IMFAttributes)activate).TryGetString(PInvoke.MFT_FRIENDLY_NAME_Attribute) ?? "Без имени";
    }

    public static string? DeviceUrl(IMFActivate activate)
    {
        return ((IMFAttributes)activate).TryGetString(PInvoke.MFT_ENUM_HARDWARE_URL_Attribute);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        activate.ShutdownObject();
        Marshal.ReleaseComObject(Transform);
        Marshal.ReleaseComObject(activate);
    }
}
