using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Audio;

internal sealed class AudioTrack : IDisposable
{
    private readonly List<AudioSource> sources;
    private readonly AudioEncoder encoder;
    private readonly AudioMixer mixer;
    private readonly AudioRing ring;
    private readonly Action<string> trace;
    private readonly GameProcess game;

    private long shift;
    private bool anchored;
    private bool finished;
    private bool disposed;

    private AudioTrack(
        List<AudioSource> sources,
        AudioEncoder encoder,
        AudioMixer mixer,
        AudioRing ring,
        Action<string> trace,
        GameProcess game)
    {
        this.game = game;
        this.sources = sources;
        this.encoder = encoder;
        this.mixer = mixer;
        this.ring = ring;
        this.trace = trace;
    }

    public IReadOnlyList<AudioSource> Sources => sources;

    public string Name => encoder.Name;

    public long Frames => encoder.Frames;

    public long Bytes => encoder.Bytes;

    public long Blocks => mixer.Blocks;

    public double Peak => mixer.Peak;

    public long Clipped => mixer.Clipped;

    public AudioRingStatus Status => ring.Status();

    public bool MicrophoneMissing => sources.Any(source =>
        source.Kind is AudioSourceKind.Microphone && source.State is not AudioSourceState.Live);

    public static SourceHealth Describe(AudioSource source)
    {
        return new SourceHealth(
            AudioSourceSet.Key(source.Kind),
            source.Label,
            source.State is AudioSourceState.Live,
            source.Complaint);
    }

    public static AudioTrack? TryStart(
        AudioTrackSettings settings,
        uint? gameProcessId,
        TimeSpan bufferLength,
        TimeSpan mergeMaxLength,
        Action<string> trace,
        Action<SourceHealth>? health = null)
    {
        List<AudioSource>? sources = null;
        AudioEncoder? encoder = null;
        AudioMixer? mixer = null;

        try
        {
            var game = new GameProcess(gameProcessId);
            sources = AudioSourceSet.Gather(new AudioSourceSetRequest(settings, game, trace));

            if (health is not null)
            {
                foreach (var source in sources)
                {
                    source.Changed = changed => health(Describe(changed));
                }
            }

            if (sources.Count is 0)
            {
                return null;
            }

            var ring = new AudioRing(AudioRing.EstimateBytes(
                AudioEncoder.BitrateBps,
                bufferLength,
                mergeMaxLength));

            encoder = new AudioEncoder((payload, time, duration) => ring.Append(payload, time, duration));

            mixer = new AudioMixer(new AudioMixRequest(Qpc.Ticks(), sources, encoder.Write, trace));
            mixer.Start();

            return new AudioTrack(sources, encoder, mixer, ring, trace, game);
        }
        catch (Exception ex)
        {
            Log.Error("аудиодорожка не поднялась, сессия идёт без звука", ex);

            mixer?.Dispose();
            encoder?.Dispose();

            if (mixer is null && sources is not null)
            {
                foreach (var source in sources)
                {
                    source.Dispose();
                }
            }

            return null;
        }
    }

    public void Retarget(uint? processId)
    {
        game.Id = processId;

        foreach (var source in sources)
        {
            if (source.Game)
            {
                source.Reopen();
            }
        }
    }

    public void Anchor(long originTicks)
    {
        if (anchored)
        {
            return;
        }

        Volatile.Write(ref shift, mixer.Origin - originTicks);
        Volatile.Write(ref anchored, true);
        trace($"аудио: сдвиг относительно видео {TimeSpan.FromTicks(Volatile.Read(ref shift)).TotalMilliseconds:F1} мс");
    }

    public void Trim(long floorTicks)
    {
        if (!Volatile.Read(ref anchored))
        {
            return;
        }

        ring.Trim(floorTicks - Volatile.Read(ref shift));
    }

    public AudioTake? Take(long fromTicks, long limitTicks)
    {
        if (!Volatile.Read(ref anchored))
        {
            return null;
        }

        var offset = Volatile.Read(ref shift);
        var take = ring.Take(fromTicks - offset, limitTicks - offset);

        if (take is null)
        {
            return null;
        }

        var moved = new List<AudioFrameEntry>(take.Frames.Count);

        foreach (var entry in take.Frames)
        {
            moved.Add(entry with { TimestampTicks = entry.TimestampTicks + offset });
        }

        return take with { Frames = moved };
    }

    public IMFMediaType? TryCloneEncodedType()
    {
        return encoder.TryCloneEncodedType();
    }

    public void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;

        mixer.Stop();
        encoder.Finish();

        Log.Report($"Звук за сессию: {mixer.Summary()}");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        try
        {
            Finish();
        }
        catch (Exception ex)
        {
            Log.Error("аудиодорожка не закрылась штатно", ex);
        }

        mixer.Dispose();
        encoder.Dispose();
    }
}
