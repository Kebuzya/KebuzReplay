using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal static class VideoMediaTypes
{
    public static IMFMediaType CreateH264Output(VideoEncoderSettings settings)
    {
        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();
        type.SetGuidValue(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        type.SetGuidValue(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
        type.SetUInt32Value(PInvoke.MF_MT_AVG_BITRATE, settings.BitrateBps);
        type.SetUInt32Value(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        type.SetUInt32Value(PInvoke.MF_MT_MPEG2_PROFILE, (uint)eAVEncH264VProfile.eAVEncH264VProfile_High);
        type.SetSizeValue(PInvoke.MF_MT_FRAME_SIZE, settings.Width, settings.Height);
        type.SetRatioValue(PInvoke.MF_MT_FRAME_RATE, settings.FrameRate, 1);
        type.SetRatioValue(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        return type;
    }

    public static IMFMediaType Clone(IMFMediaType source)
    {
        PInvoke.MFCreateMediaType(out var copy).ThrowOnFailure();
        source.CopyAllItems(copy);
        return copy;
    }

    public static IMFMediaType CreateUncompressedInput(Guid subtype, uint width, uint height, uint frameRate)
    {
        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();
        type.SetGuidValue(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        type.SetGuidValue(PInvoke.MF_MT_SUBTYPE, subtype);
        type.SetUInt32Value(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        type.SetUInt32Value(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
        type.SetSizeValue(PInvoke.MF_MT_FRAME_SIZE, width, height);
        type.SetRatioValue(PInvoke.MF_MT_FRAME_RATE, frameRate, 1);
        type.SetRatioValue(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        return type;
    }
}
