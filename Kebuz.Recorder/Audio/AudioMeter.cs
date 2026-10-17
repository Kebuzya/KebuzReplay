using System.Globalization;
using Kebuz.Recorder.Ipc;

namespace Kebuz.Recorder.Audio;

internal static class AudioMeter
{
    private static volatile bool enabled;
    private static Action<AudioLevels>? sink;
    private static int busy;

    public static bool Enabled => enabled;

    public static void Attach(Action<AudioLevels>? target)
    {
        Volatile.Write(ref sink, target);
    }

    public static void Watch(bool on)
    {
        enabled = on;
    }

    public static void Publish(AudioLevels levels, bool final = false)
    {
        var target = Volatile.Read(ref sink);

        if (target is null)
        {
            return;
        }

        if (!final && Interlocked.Exchange(ref busy, 1) == 1)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            _ =>
            {
                try
                {
                    target(levels);
                }
                finally
                {
                    if (!final)
                    {
                        Volatile.Write(ref busy, 0);
                    }
                }
            },
            null);
    }
}

internal sealed class AudioLevelTally
{
    public const int WindowBlocks = 10;

    private static readonly double VoicedFloor = Math.Pow(10, -50 / 20d);

    private readonly IReadOnlyList<AudioSource> sources;
    private readonly bool preview;
    private readonly int windowSamples;
    private readonly float[] peaks;
    private readonly double[] energies;
    private readonly float[] sessionPeaks;
    private readonly double[] loudest;
    private readonly double[] voicedEnergy;
    private readonly long[] voicedWindows;

    private float mixPeak;
    private double mixEnergy;
    private long limitedBefore;
    private int blocks;
    private bool published;

    public AudioLevelTally(IReadOnlyList<AudioSource> sources, int blockSamples, bool preview)
    {
        this.sources = sources;
        this.preview = preview;
        windowSamples = blockSamples * WindowBlocks;
        peaks = new float[sources.Count];
        energies = new double[sources.Count];
        sessionPeaks = new float[sources.Count];
        loudest = new double[sources.Count];
        voicedEnergy = new double[sources.Count];
        voicedWindows = new long[sources.Count];
    }

    public void Source(int index, float peak, double energy)
    {
        if (peak > peaks[index])
        {
            peaks[index] = peak;
        }

        energies[index] += energy;
    }

    public void Block(float blockMixPeak, double blockMixEnergy, long limited)
    {
        mixEnergy += blockMixEnergy;

        if (blockMixPeak > mixPeak)
        {
            mixPeak = blockMixPeak;
        }

        if (++blocks < WindowBlocks)
        {
            return;
        }

        var levels = AudioMeter.Enabled ? new List<SourceLevel>(sources.Count) : null;

        for (var i = 0; i < sources.Count; i++)
        {
            var rms = Math.Sqrt(energies[i] / windowSamples);

            if (peaks[i] > sessionPeaks[i])
            {
                sessionPeaks[i] = peaks[i];
            }

            if (rms > loudest[i])
            {
                loudest[i] = rms;
            }

            if (rms > VoicedFloor)
            {
                voicedEnergy[i] += rms * rms;
                voicedWindows[i]++;
            }

            levels?.Add(new SourceLevel(
                AudioSourceSet.MeterKey(sources[i]),
                sources[i].Title,
                peaks[i],
                (float)rms));

            peaks[i] = 0;
            energies[i] = 0;
        }

        if (levels is not null)
        {
            AudioMeter.Publish(new AudioLevels(
                levels,
                mixPeak,
                (float)Math.Sqrt(mixEnergy / windowSamples),
                limited > limitedBefore,
                preview));
            published = true;
        }

        mixPeak = 0;
        mixEnergy = 0;
        limitedBefore = limited;
        blocks = 0;
    }

    public void Silence()
    {
        if (published)
        {
            AudioMeter.Publish(new AudioLevels([], 0, 0, false, preview), true);
            published = false;
        }
    }

    public string Summary(double mixPeakTotal, long limitedFrames, float deepestGain, long clippedTotal)
    {
        var parts = new List<string>(sources.Count + 1);

        for (var i = 0; i < sources.Count; i++)
        {
            var voiced = voicedWindows[i] > 0 ? Math.Sqrt(voicedEnergy[i] / voicedWindows[i]) : 0;
            var voicedSeconds = voicedWindows[i] * WindowBlocks / 100d;

            parts.Add(
                $"{sources[i].Title}: пик {Db(sessionPeaks[i])}, RMS громкого места {Db(loudest[i])}, " +
                $"RMS звучания {Db(voiced)} за {voicedSeconds.ToString("F1", CultureInfo.InvariantCulture)} с");
        }

        parts.Add(
            $"микс: пик до лимитера {Db(mixPeakTotal)}, лимитер прижимал " +
            $"{(limitedFrames / (double)AudioMixer.SampleRate).ToString("F1", CultureInfo.InvariantCulture)} с, " +
            $"глубже всего на {(-AudioGain.Decibels(deepestGain)).ToString("F1", CultureInfo.InvariantCulture)} дБ, " +
            $"срезано отсчётов после лимитера {clippedTotal}");

        return string.Join("; ", parts);
    }

    private static string Db(double amplitude)
    {
        return amplitude > 0
            ? $"{AudioGain.Decibels(amplitude).ToString("F1", CultureInfo.InvariantCulture)} дБ"
            : "тишина";
    }
}
