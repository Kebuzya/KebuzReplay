namespace Kebuz.Recorder.Audio;

public sealed class AudioLimiter
{
    public const float Ceiling = 0.891f;
    public const int LookaheadFrames = 96;

    private const double ReleaseSeconds = 0.15;
    private const float Engaged = 0.99f;

    private readonly int channels;
    private readonly float[] delay;
    private readonly float[] required;
    private readonly float attack;
    private readonly float release;

    private int delayHead;
    private int requiredHead;
    private float gain = 1f;
    private float windowMin = 1f;

    public AudioLimiter(int sampleRate, int channels)
    {
        this.channels = channels;
        delay = new float[LookaheadFrames * channels];
        required = new float[LookaheadFrames + 1];
        Array.Fill(required, 1f);
        attack = 1f - (float)Math.Exp(Math.Log(0.0001) / LookaheadFrames);
        release = (float)Math.Exp(-1d / (ReleaseSeconds * sampleRate));
    }

    public long LimitedFrames { get; private set; }

    public float DeepestGain { get; private set; } = 1f;

    public float Gain => gain;

    public void Process(Span<float> interleaved)
    {
        for (var frame = 0; frame + channels <= interleaved.Length; frame += channels)
        {
            var peak = 0f;

            for (var channel = 0; channel < channels; channel++)
            {
                var magnitude = Math.Abs(interleaved[frame + channel]);

                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            Admit(peak > Ceiling ? Ceiling / peak : 1f);

            if (windowMin < gain)
            {
                gain -= (gain - windowMin) * attack;
            }
            else
            {
                gain = windowMin + ((gain - windowMin) * release);
            }

            if (gain < Engaged)
            {
                LimitedFrames++;
            }

            if (gain < DeepestGain)
            {
                DeepestGain = gain;
            }

            var slot = delayHead * channels;

            for (var channel = 0; channel < channels; channel++)
            {
                var incoming = interleaved[frame + channel];
                interleaved[frame + channel] = delay[slot + channel] * gain;
                delay[slot + channel] = incoming;
            }

            delayHead = (delayHead + 1) % LookaheadFrames;
        }
    }

    private void Admit(float next)
    {
        var leaving = required[requiredHead];
        required[requiredHead] = next;
        requiredHead = (requiredHead + 1) % required.Length;

        if (next <= windowMin)
        {
            windowMin = next;
            return;
        }

        if (leaving > windowMin)
        {
            return;
        }

        var lowest = 1f;

        foreach (var value in required)
        {
            if (value < lowest)
            {
                lowest = value;
            }
        }

        windowMin = lowest;
    }
}
