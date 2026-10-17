using Kebuz.Recorder.Encoding;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Audio;

internal static class AudioTypes
{
    public static IMFMediaType Pcm(uint rate, uint channels, bool floating)
    {
        var bits = floating ? 32u : 16u;

        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();

        type.SetGuidValue(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
        type.SetGuidValue(PInvoke.MF_MT_SUBTYPE, floating ? PInvoke.MFAudioFormat_Float : PInvoke.MFAudioFormat_PCM);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, channels);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * bits / 8);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, rate * channels * bits / 8);
        type.SetUInt32Value(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

        return type;
    }

    public static string Describe(IMFMediaType type)
    {
        var subtype = type.TryGetGuid(PInvoke.MF_MT_SUBTYPE);
        var name = subtype == PInvoke.MFAudioFormat_Float ? "float"
            : subtype == PInvoke.MFAudioFormat_PCM ? "pcm"
            : subtype?.ToString() ?? "?";

        return $"{type.TryGetUInt32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND)}/" +
            $"{type.TryGetUInt32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS)}/" +
            $"{name}{type.TryGetUInt32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE)}";
    }
}
