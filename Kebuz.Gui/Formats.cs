using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui;

internal static class Formats
{
    public static string Resolution(QualityProfile profile)
    {
        if (profile.TargetHeight is 0)
        {
            return Strings.Get("Format.NoScale");
        }

        var width = QualityPresets.Even(profile.TargetHeight * 16 / 9);
        return $"{width}x{profile.TargetHeight}";
    }

    public static string Output(QualityProfile profile)
    {
        return $"{Resolution(profile)}@{profile.FrameRate}";
    }

    public static long ClipBytes(QualityProfile profile, double seconds, AudioSettings audio)
    {
        var video = (long)(profile.MeanBitrateBps / 8d * seconds);

        return audio.AnySource ? video + AudioBudget.ClipBytes(seconds) : video;
    }

    public static long BufferBytes(QualityProfile profile, double bufferSeconds, double mergeMaxSeconds, AudioSettings audio)
    {
        var video = (long)(profile.PeakBitrateBps / 8d * (bufferSeconds + mergeMaxSeconds) * 1.25);

        if (!audio.AnySource)
        {
            return video;
        }

        return video + AudioBudget.RingBytes(
            TimeSpan.FromSeconds(bufferSeconds),
            TimeSpan.FromSeconds(mergeMaxSeconds));
    }

    public static long LimitBytes(double gigabytes)
    {
        return (long)(gigabytes * 1024 * 1024 * 1024);
    }
}
