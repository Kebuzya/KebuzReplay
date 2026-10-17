using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Recorder.Audio;

internal delegate void AudioBlockHandler(ReadOnlySpan<short> samples, long frame);

internal sealed record AudioMixRequest(
    long Origin,
    IReadOnlyList<AudioSource> Sources,
    AudioBlockHandler OnBlock,
    Action<string> Trace,
    bool Preview = false);

internal sealed class AudioMixer : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BlockFrames = SampleRate / 100;

    private const long GridTicks = TimeSpan.TicksPerSecond / 100;
    private const long LagTicks = TimeSpan.TicksPerMillisecond * 100;

    private static readonly TimeSpan TendInterval = TimeSpan.FromSeconds(1);

    private readonly AudioMixRequest request;
    private readonly ManualResetEventSlim stopping = new(false);
    private readonly Thread mixer;
    private readonly Thread tender;
    private readonly AudioLevelTally tally;
    private readonly AudioLimiter limiter = new(SampleRate, Channels);

    private long blocks;
    private bool started;
    private bool disposed;

    public AudioMixer(AudioMixRequest request)
    {
        this.request = request;

        mixer = new Thread(Mix)
        {
            Name = "KebuzAudioMix",
            IsBackground = true
        };

        tender = new Thread(Tend)
        {
            Name = "KebuzAudioTend",
            IsBackground = true
        };

        mixer.SetApartmentState(ApartmentState.MTA);
        tender.SetApartmentState(ApartmentState.MTA);

        foreach (var source in request.Sources)
        {
            source.Bind(new AudioSourceRing(request.Origin, source.Title, request.Trace));
        }

        tally = new AudioLevelTally(request.Sources, BlockFrames * Channels, request.Preview);
    }

    public long Origin => request.Origin;

    public IReadOnlyList<AudioSource> Sources => request.Sources;

    public long Blocks => Volatile.Read(ref blocks);

    public long Clipped { get; private set; }

    public double Peak { get; private set; }

    public void Start()
    {
        if (started)
        {
            return;
        }

        started = true;

        foreach (var source in request.Sources)
        {
            if (!source.Open())
            {
                request.Trace($"{source.Title}: не открылся, {Localization.Strings.Render(source.Complaint)}");
            }
        }

        mixer.Start();
        tender.Start();
    }

    public void Stop()
    {
        if (!started)
        {
            return;
        }

        started = false;
        stopping.Set();
        mixer.Join(TimeSpan.FromSeconds(2));
        tender.Join(TimeSpan.FromSeconds(2));
        tally.Silence();

        foreach (var source in request.Sources)
        {
            source.Close();
        }
    }

    private void Mix()
    {
        var scratch = new float[BlockFrames * Channels];
        var accumulator = new float[BlockFrames * Channels];
        var output = new short[BlockFrames * Channels];

        while (true)
        {
            var due = request.Origin + ((blocks + 1) * GridTicks) + LagTicks;
            var wait = due - Qpc.Ticks();

            if (wait > 0 && stopping.Wait(TimeSpan.FromTicks(wait)))
            {
                break;
            }

            Emit(scratch, accumulator, output);
        }

        var last = Qpc.Ticks() - LagTicks;

        while (request.Origin + ((blocks + 1) * GridTicks) <= last)
        {
            Emit(scratch, accumulator, output);
        }
    }

    private void Emit(float[] scratch, float[] accumulator, short[] output)
    {
        Array.Clear(accumulator);

        for (var index = 0; index < request.Sources.Count; index++)
        {
            var source = request.Sources[index];

            source.Ring.Read(scratch);

            var gain = source.Gain;
            var peak = 0f;
            var energy = 0d;

            for (var i = 0; i < accumulator.Length; i++)
            {
                var sample = scratch[i];
                var magnitude = Math.Abs(sample);

                if (magnitude > peak)
                {
                    peak = magnitude;
                }

                energy += sample * sample;
                accumulator[i] += sample * gain;
            }

            tally.Source(index, peak, energy);
        }

        var blockPeak = 0f;
        var blockEnergy = 0d;

        for (var i = 0; i < accumulator.Length; i++)
        {
            var value = accumulator[i];
            var magnitude = Math.Abs(value);

            if (magnitude > blockPeak)
            {
                blockPeak = magnitude;
            }

            blockEnergy += value * value;
        }

        limiter.Process(accumulator);

        for (var i = 0; i < output.Length; i++)
        {
            var value = accumulator[i];

            if (value > 1f)
            {
                value = 1f;
                Clipped++;
            }
            else if (value < -1f)
            {
                value = -1f;
                Clipped++;
            }

            output[i] = (short)(value * short.MaxValue);
        }

        if (blockPeak > Peak)
        {
            Peak = blockPeak;
        }

        tally.Block(blockPeak, blockEnergy, limiter.LimitedFrames);
        request.OnBlock(output, blocks * BlockFrames);
        Volatile.Write(ref blocks, blocks + 1);
    }

    public string Summary()
    {
        return tally.Summary(Peak, limiter.LimitedFrames, limiter.DeepestGain, Clipped);
    }

    private void Tend()
    {
        while (!stopping.Wait(TendInterval))
        {
            foreach (var source in request.Sources)
            {
                source.Tend();
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        Stop();
        stopping.Dispose();

        foreach (var source in request.Sources)
        {
            source.Dispose();
        }
    }
}
