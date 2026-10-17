namespace Kebuz.Recorder.Audio;

public static class AudioBudget
{
    public static uint BitrateBps => AudioEncoder.BitrateBps;

    public static long RingBytes(TimeSpan bufferLength, TimeSpan mergeMaxLength)
    {
        return AudioRing.EstimateBytes(AudioEncoder.BitrateBps, bufferLength, mergeMaxLength);
    }

    public static long ClipBytes(double seconds)
    {
        return (long)(AudioEncoder.BitrateBps / 8d * seconds);
    }
}
