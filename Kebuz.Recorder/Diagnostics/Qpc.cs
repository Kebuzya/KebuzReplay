using System.Diagnostics;

namespace Kebuz.Recorder.Diagnostics;

internal static class Qpc
{
    private static readonly double Scale = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    public static long Ticks()
    {
        return FromTimestamp(Stopwatch.GetTimestamp());
    }

    public static long FromTimestamp(long timestamp) => (long)(timestamp * Scale);
}
