namespace Kebuz.Recorder.Audio;

public static class AudioGain
{
    public static float Of(double level)
    {
        return level == 100 ? 1f : (float)Math.Pow(Math.Max(level, 0) / 100d, 3);
    }

    public static double Decibels(double amplitude)
    {
        return amplitude > 0 ? 20 * Math.Log10(amplitude) : double.NegativeInfinity;
    }
}
