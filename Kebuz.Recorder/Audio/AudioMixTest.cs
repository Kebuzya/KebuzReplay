using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Settings;

namespace Kebuz.Recorder.Audio;

public sealed record AudioTestSource(
    string Id,
    string Title,
    bool Live,
    string? Complaint,
    bool HasSignal);

public sealed class AudioTestTake
{
    private readonly IReadOnlyList<float[]> stems;
    private readonly string shape;
    private readonly int frames;

    internal AudioTestTake(
        IReadOnlyList<AudioTestSource> sources,
        IReadOnlyList<float[]> stems,
        string shape,
        int frames)
    {
        Sources = sources;
        this.stems = stems;
        this.shape = shape;
        this.frames = frames;
    }

    public IReadOnlyList<AudioTestSource> Sources { get; }

    public bool HasSignal => Sources.Any(source => source.HasSignal);

    public bool Matches(AudioSettings settings, uint? gameProcessId)
    {
        if (shape != AudioMixTest.Shape(settings, gameProcessId))
        {
            return false;
        }

        var needsLiveGame = settings.Mode is SystemAudioMode.Apps && settings.GameSound && gameProcessId is not null;
        return !needsLiveGame || Sources.Any(source => source.Id == "game");
    }

    public MemoryStream Render(AudioSettings settings, string? soloSource = null)
    {
        var stream = new MemoryStream(44 + (frames * AudioMixer.Channels * sizeof(short)));
        WriteHeader(stream, frames);

        var limiter = new AudioLimiter(AudioMixer.SampleRate, AudioMixer.Channels);
        var accumulator = new float[AudioMixer.BlockFrames * AudioMixer.Channels];
        var output = new byte[AudioMixer.BlockFrames * AudioMixer.Channels * sizeof(short)];

        for (var frame = 0; frame < frames; frame += AudioMixer.BlockFrames)
        {
            Array.Clear(accumulator);
            var samples = Math.Min(AudioMixer.BlockFrames, frames - frame) * AudioMixer.Channels;

            for (var source = 0; source < Sources.Count; source++)
            {
                var descriptor = Sources[source];
                var gain = soloSource is not null && descriptor.Id != soloSource
                    ? 0
                    : Gain(descriptor.Id, settings);

                if (gain == 0)
                {
                    continue;
                }

                var stem = stems[source];
                var offset = frame * AudioMixer.Channels;

                for (var sample = 0; sample < samples; sample++)
                {
                    accumulator[sample] += stem[offset + sample] * gain;
                }
            }

            limiter.Process(accumulator);

            for (var sample = 0; sample < samples; sample++)
            {
                var value = Math.Clamp(accumulator[sample], -1f, 1f);
                var pcm = (short)(value * short.MaxValue);
                output[sample * 2] = (byte)pcm;
                output[(sample * 2) + 1] = (byte)(pcm >> 8);
            }

            stream.Write(output, 0, samples * sizeof(short));
        }

        stream.Position = 0;
        return stream;
    }

    private static float Gain(string id, AudioSettings settings)
    {
        if (id == "desktop")
        {
            return settings.Mode is SystemAudioMode.Desktop ? AudioGain.Of(settings.DesktopLevel) : 0;
        }

        if (id == "game")
        {
            return settings.Mode is SystemAudioMode.Apps && settings.GameSound
                ? AudioGain.Of(settings.GameLevel)
                : 0;
        }

        if (id == "mic")
        {
            return settings.Microphone ? AudioGain.Of(settings.MicrophoneLevel) : 0;
        }

        const string prefix = "process:";

        if (!id.StartsWith(prefix, StringComparison.Ordinal))
        {
            return 0;
        }

        var executable = id[prefix.Length..];
        var app = settings.Apps.FirstOrDefault(candidate =>
            candidate.Enabled && candidate.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase));

        return settings.Mode is SystemAudioMode.Apps && app is not null ? AudioGain.Of(app.Level) : 0;
    }

    private static void WriteHeader(Stream stream, int frames)
    {
        var dataBytes = frames * AudioMixer.Channels * sizeof(short);

        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)AudioMixer.Channels);
        writer.Write(AudioMixer.SampleRate);
        writer.Write(AudioMixer.SampleRate * AudioMixer.Channels * sizeof(short));
        writer.Write((short)(AudioMixer.Channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
    }
}

public static class AudioMixTest
{
    public const int DurationSeconds = 10;

    public static Task<AudioTestTake> Capture(
        AudioSettings settings,
        uint? gameProcessId,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<AudioTestTake>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(settings, gameProcessId, cancellationToken, completion))
        {
            Name = "KebuzAudioTest",
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    internal static string Shape(AudioSettings settings, uint? gameProcessId)
    {
        var track = AudioTrackSettings.Of(settings);
        return $"{track.Desktop}|{track.DesktopDevice}|{track.GameSound}|" +
            $"{track.Microphone}|{track.MicrophoneDevice}|{track.MicrophoneDenoise}|" +
            string.Join("|", track.Apps.Select(app => app.Executable.ToUpperInvariant()));
    }

    private static void Run(
        AudioSettings settings,
        uint? gameProcessId,
        CancellationToken cancellationToken,
        TaskCompletionSource<AudioTestTake> completion)
    {
        AudioMixer? mixer = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var track = AudioTrackSettings.Of(settings);
            var game = new GameProcess(gameProcessId);
            var sources = AudioSourceSet.Gather(new AudioSourceSetRequest(
                track,
                game,
                message => Log.Trace($"проверка микса: {message}")));

            if (sources.Count is 0)
            {
                throw new InvalidOperationException(Localization.Strings.Code("Settings.Sound.TestNoSources"));
            }

            using var recorder = new StemRecorder(sources.Count, DurationSeconds * AudioMixer.SampleRate);
            mixer = new AudioMixer(new AudioMixRequest(
                Qpc.Ticks(),
                sources,
                recorder.Block,
                message => Log.Trace($"проверка микса: {message}"),
                OnSource: recorder.Source));

            mixer.Start();

            var signaled = WaitHandle.WaitAny([recorder.Done.WaitHandle, cancellationToken.WaitHandle]);

            if (signaled == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            mixer.Stop();
            completion.SetResult(recorder.Take(sources, Shape(settings, gameProcessId)));
        }
        catch (OperationCanceledException)
        {
            completion.SetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error("проверка микса не записалась", ex);
            completion.SetException(ex);
        }
        finally
        {
            mixer?.Dispose();
        }
    }

    private sealed class StemRecorder : IDisposable
    {
        private readonly float[][] stems;
        private readonly float[] peaks;
        private readonly int frames;

        public StemRecorder(int sources, int frames)
        {
            this.frames = frames;
            stems = new float[sources][];
            peaks = new float[sources];

            for (var source = 0; source < sources; source++)
            {
                stems[source] = new float[frames * AudioMixer.Channels];
            }
        }

        public ManualResetEventSlim Done { get; } = new(false);

        public void Source(int source, ReadOnlySpan<float> samples, long frame)
        {
            if (frame >= frames)
            {
                return;
            }

            var count = Math.Min(samples.Length, (frames - (int)frame) * AudioMixer.Channels);
            samples[..count].CopyTo(stems[source].AsSpan((int)frame * AudioMixer.Channels, count));

            for (var index = 0; index < count; index++)
            {
                peaks[source] = Math.Max(peaks[source], Math.Abs(samples[index]));
            }
        }

        public void Block(ReadOnlySpan<short> samples, long frame)
        {
            if (frame + AudioMixer.BlockFrames >= frames)
            {
                Done.Set();
            }
        }

        public AudioTestTake Take(IReadOnlyList<AudioSource> sources, string shape)
        {
            var descriptors = new AudioTestSource[sources.Count];

            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                descriptors[index] = new AudioTestSource(
                    Id(source),
                    source.Title,
                    source.State is AudioSourceState.Live,
                    source.Complaint,
                    peaks[index] > 0);
            }

            return new AudioTestTake(descriptors, stems, shape, frames);
        }

        private static string Id(AudioSource source)
        {
            if (source.Game)
            {
                return "game";
            }

            return source.Kind switch
            {
                AudioSourceKind.Desktop => "desktop",
                AudioSourceKind.Microphone => "mic",
                _ => $"process:{source.Label}"
            };
        }

        public void Dispose()
        {
            Done.Dispose();
        }
    }
}
