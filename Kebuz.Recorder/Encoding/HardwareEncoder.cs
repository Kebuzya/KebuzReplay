using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal static class HardwareEncoder
{
    private const MFT_ENUM_FLAG EnumFlags =
        MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;

    public static MediaTransform Open()
    {
        return MediaTransform.Open(
            PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
            EnumFlags,
            PInvoke.MFMediaType_Video,
            PInvoke.MFVideoFormat_H264,
            "Аппаратный H264-энкодер");
    }

    public static IMFActivate[] Enumerate()
    {
        return MediaTransform.Enumerate(
            PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
            EnumFlags,
            PInvoke.MFMediaType_Video,
            PInvoke.MFVideoFormat_H264);
    }

    public static string FriendlyName(IMFActivate activate)
    {
        return MediaTransform.FriendlyName(activate);
    }

    public static string? DeviceUrl(IMFActivate activate)
    {
        return MediaTransform.DeviceUrl(activate);
    }
}
