using System.Runtime.InteropServices;
using Kebuz.Recorder.Encoding;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Kebuz.Recorder.Audio;

internal delegate void FilteredChunkHandler(ReadOnlySpan<float> interleaved, int frames, long qpcTicks, bool discontinuity);

internal sealed class MicrophoneFilter : IDisposable
{
    public const uint DspRate = 22050;
    public const int LatencyFrames = 557;

    private static readonly Guid ResamplerClsid = new("F447B69E-1884-4A7E-8055-346F74D6EDB3");

    private readonly IMFTransform dsp;
    private readonly IMFTransform resampler;
    private readonly SyncTransformPump first;
    private readonly SyncTransformPump second;
    private readonly bool floatOutput;
    private readonly float[] staging = new float[AudioMixer.BlockFrames * AudioMixer.Channels];

    private FilteredChunkHandler? sink;
    private float[] converted = new float[AudioMixer.BlockFrames * AudioMixer.Channels * 4];
    private int staged;
    private long inputFrames;
    private long outputFrames;
    private long dspFrames;
    private long blockEndQpc;
    private bool discontinuity;
    private bool disposed;

    public MicrophoneFilter()
    {
        MediaFoundationRuntime.EnsureStarted();

        dsp = VoiceCaptureDsp.Create();

        try
        {
            PInvoke.CoCreateInstance<IMFTransform>(ResamplerClsid, null, CLSCTX.CLSCTX_INPROC_SERVER, out resampler)
                .ThrowOnFailure();

            var store = (IPropertyStore)dsp;
            VoiceCaptureDsp.SetBool(store, VoiceCaptureDsp.SourceMode, false);
            VoiceCaptureDsp.SetInt(store, VoiceCaptureDsp.SystemMode, (int)AEC_SYSTEM_MODE.SINGLE_CHANNEL_NSAGC);
            VoiceCaptureDsp.SetBool(store, VoiceCaptureDsp.FeatureMode, true);
            VoiceCaptureDsp.SetInt(store, VoiceCaptureDsp.NoiseSuppression, 1);
            VoiceCaptureDsp.SetBool(store, VoiceCaptureDsp.GainControl, false);

            SetTypes(dsp, AudioTypes.Pcm(AudioMixer.SampleRate, AudioMixer.Channels, true), AudioTypes.Pcm(DspRate, 1, false));

            try
            {
                SetTypes(resampler, AudioTypes.Pcm(DspRate, 1, false), AudioTypes.Pcm(AudioMixer.SampleRate, AudioMixer.Channels, true));
                floatOutput = true;
            }
            catch (COMException)
            {
                SetTypes(resampler, AudioTypes.Pcm(DspRate, 1, false), AudioTypes.Pcm(AudioMixer.SampleRate, AudioMixer.Channels, false));
            }

            first = new SyncTransformPump(dsp);
            second = new SyncTransformPump(resampler);
        }
        catch
        {
            if (resampler is not null)
            {
                Marshal.ReleaseComObject(resampler);
            }

            Marshal.ReleaseComObject(dsp);
            throw;
        }
    }

    public bool FloatOutput => floatOutput;

    public long InputFrames => inputFrames;

    public long OutputFrames => outputFrames;

    public void Push(ReadOnlySpan<float> interleaved, int frames, long qpcTicks, bool gap, FilteredChunkHandler onOutput)
    {
        sink = onOutput;

        if (gap)
        {
            discontinuity = true;
        }

        var taken = 0;

        while (taken < frames)
        {
            var room = AudioMixer.BlockFrames - staged;
            var count = Math.Min(room, frames - taken);

            interleaved.Slice(taken * AudioMixer.Channels, count * AudioMixer.Channels)
                .CopyTo(staging.AsSpan(staged * AudioMixer.Channels));

            staged += count;
            taken += count;

            if (staged < AudioMixer.BlockFrames)
            {
                continue;
            }

            blockEndQpc = qpcTicks + (taken * TimeSpan.TicksPerSecond / AudioMixer.SampleRate);
            inputFrames += AudioMixer.BlockFrames;
            staged = 0;

            first.Push(
                MemoryMarshal.AsBytes(staging.AsSpan()),
                (inputFrames - AudioMixer.BlockFrames) * TimeSpan.TicksPerSecond / AudioMixer.SampleRate,
                AudioMixer.BlockFrames * TimeSpan.TicksPerSecond / AudioMixer.SampleRate,
                Forward);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        first.Dispose();
        second.Dispose();
        Marshal.ReleaseComObject(resampler);
        Marshal.ReleaseComObject(dsp);
    }

    private void Forward(ReadOnlySpan<byte> payload)
    {
        var frames = payload.Length / sizeof(short);
        var time = dspFrames * TimeSpan.TicksPerSecond / DspRate;
        var length = frames * TimeSpan.TicksPerSecond / DspRate;

        dspFrames += frames;
        second.Push(payload, time, length, Deliver);
    }

    private void Deliver(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<float> samples;

        if (floatOutput)
        {
            samples = MemoryMarshal.Cast<byte, float>(payload);
        }
        else
        {
            var shorts = MemoryMarshal.Cast<byte, short>(payload);

            if (converted.Length < shorts.Length)
            {
                converted = new float[shorts.Length];
            }

            for (var i = 0; i < shorts.Length; i++)
            {
                converted[i] = shorts[i] / 32768f;
            }

            samples = converted.AsSpan(0, shorts.Length);
        }

        var frames = samples.Length / AudioMixer.Channels;

        if (frames is 0)
        {
            return;
        }

        var lag = inputFrames - outputFrames + LatencyFrames;
        var qpc = blockEndQpc - (lag * TimeSpan.TicksPerSecond / AudioMixer.SampleRate);

        outputFrames += frames;

        var gap = discontinuity;
        discontinuity = false;

        sink?.Invoke(samples.Slice(0, frames * AudioMixer.Channels), frames, qpc, gap);
    }

    private static void SetTypes(IMFTransform transform, IMFMediaType input, IMFMediaType output)
    {
        try
        {
            try
            {
                transform.SetInputType(0, input, 0);
                transform.SetOutputType(0, output, 0);
            }
            catch (COMException)
            {
                transform.SetOutputType(0, output, 0);
                transform.SetInputType(0, input, 0);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(input);
            Marshal.ReleaseComObject(output);
        }
    }
}
