namespace Kebuz.Recorder.Audio;

internal sealed class AudioSourceRing
{
    private const int CapacityFrames = AudioMixer.SampleRate;
    private const int AlignMilliseconds = 30;

    private readonly Lock gate = new();
    private readonly float[] samples = new float[CapacityFrames * AudioMixer.Channels];
    private readonly long threshold = AudioMixer.SampleRate * AlignMilliseconds / 1000;
    private readonly long origin;
    private readonly string title;
    private readonly Action<string> trace;

    private long head;
    private long expected;
    private long filled;
    private bool aligned;
    private bool started;

    public AudioSourceRing(long origin, string title, Action<string> trace)
    {
        this.origin = origin;
        this.title = title;
        this.trace = trace;
    }

    public long Written { get; private set; }

    public long Gaps { get; private set; }

    public long Dropped { get; private set; }

    public long Idle { get; private set; }

    public int Corrections { get; private set; }

    public void Realign()
    {
        lock (gate)
        {
            aligned = false;
        }
    }

    public void Write(ReadOnlySpan<float> interleaved, int frames, long qpcTicks, bool discontinuity)
    {
        lock (gate)
        {
            var stamped = (long)((qpcTicks - origin) * (double)AudioMixer.SampleRate / TimeSpan.TicksPerSecond);
            long position;

            if (!aligned || discontinuity)
            {
                position = stamped;
                aligned = true;
            }
            else
            {
                var drift = stamped - expected;

                if (Math.Abs(drift) > threshold)
                {
                    Corrections++;
                    trace($"{title}: расхождение с сеткой {drift * 1000d / AudioMixer.SampleRate:F0} мс, правлю позицию");
                    position = stamped;
                }
                else
                {
                    position = expected;
                }
            }

            if (started && position > expected)
            {
                Gaps += position - expected;
            }

            started = true;
            expected = position + frames;

            var offset = 0;
            var length = frames;

            if (position < head)
            {
                var late = head - position;

                if (late >= length)
                {
                    Dropped += length;
                    return;
                }

                offset = (int)late;
                length -= (int)late;
                position = head;
                Dropped += late;
            }

            var ceiling = head + CapacityFrames;

            if (position + length > ceiling)
            {
                var over = position + length - ceiling;

                if (over >= length)
                {
                    Dropped += length;
                    return;
                }

                Dropped += over;
                length -= (int)over;
            }

            Copy(interleaved.Slice(offset * AudioMixer.Channels, length * AudioMixer.Channels), position, length);

            Written += length;
            filled = position + length;
        }
    }

    public void Read(Span<float> destination)
    {
        lock (gate)
        {
            var frames = destination.Length / AudioMixer.Channels;

            if (filled <= head)
            {
                Idle += frames;
            }

            var index = (int)(head % CapacityFrames);
            var first = Math.Min(frames, CapacityFrames - index);

            var front = samples.AsSpan(index * AudioMixer.Channels, first * AudioMixer.Channels);
            front.CopyTo(destination);
            front.Clear();

            if (first < frames)
            {
                var back = samples.AsSpan(0, (frames - first) * AudioMixer.Channels);
                back.CopyTo(destination[(first * AudioMixer.Channels)..]);
                back.Clear();
            }

            head += frames;
        }
    }

    private void Copy(ReadOnlySpan<float> source, long position, int frames)
    {
        var index = (int)(position % CapacityFrames);
        var first = Math.Min(frames, CapacityFrames - index);

        source[..(first * AudioMixer.Channels)].CopyTo(samples.AsSpan(index * AudioMixer.Channels));

        if (first < frames)
        {
            source[(first * AudioMixer.Channels)..].CopyTo(samples.AsSpan(0));
        }
    }
}
