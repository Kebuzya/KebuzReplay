using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Recorder.Audio;

internal sealed class AudioPreview : IDisposable
{
    private readonly Lock gate = new();
    private readonly Action<string> trace;

    private AudioTrackSettings settings;
    private AudioMixer? mixer;
    private bool wanted;
    private bool recording;
    private bool disposed;

    public AudioPreview(AudioTrackSettings settings, Action<string> trace)
    {
        this.settings = settings;
        this.trace = trace;
    }

    public bool Open
    {
        get
        {
            lock (gate)
            {
                return mixer is not null;
            }
        }
    }

    public void Watch(bool on)
    {
        lock (gate)
        {
            wanted = on;
            Sync();
        }
    }

    public void Recording(bool on)
    {
        lock (gate)
        {
            recording = on;
            Sync();
        }
    }

    public void Apply(AudioTrackSettings next)
    {
        lock (gate)
        {
            var reshaped = Shape(next) != Shape(settings);

            settings = next;

            if (mixer is null)
            {
                return;
            }

            if (reshaped)
            {
                Close();
                Sync();
                return;
            }

            foreach (var source in mixer.Sources)
            {
                source.Level = LevelOf(source, next);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Close();
        }
    }

    private void Sync()
    {
        if (disposed)
        {
            return;
        }

        var should = wanted && !recording;

        if (should && mixer is null)
        {
            Start();
        }
        else if (!should && mixer is not null)
        {
            Close();
        }
    }

    private void Start()
    {
        try
        {
            var sources = AudioSourceSet.Gather(new AudioSourceSetRequest(
                settings with { Microphone = true },
                null,
                trace));

            if (sources.Count is 0)
            {
                return;
            }

            mixer = new AudioMixer(new AudioMixRequest(Qpc.Ticks(), sources, (_, _) => { }, trace, true));
            mixer.Start();

            Log.Report($"Предпросмотр звука открыт: {string.Join(", ", sources.Select(source => source.Title))}");
        }
        catch (Exception ex)
        {
            Log.Error("предпросмотр звука не открылся", ex);
            mixer?.Dispose();
            mixer = null;
        }
    }

    private void Close()
    {
        if (mixer is null)
        {
            return;
        }

        mixer.Dispose();
        mixer = null;

        Log.Report("Предпросмотр звука закрыт");
    }

    private static int LevelOf(AudioSource source, AudioTrackSettings settings)
    {
        return source.Kind switch
        {
            AudioSourceKind.Desktop => settings.DesktopLevel,
            AudioSourceKind.Microphone => settings.MicrophoneLevel,
            _ => settings.Apps.FirstOrDefault(app => app.Executable == source.Title)?.Level ?? source.Level
        };
    }

    private static string Shape(AudioTrackSettings settings)
    {
        return $"{settings.Desktop}|{settings.DesktopDevice}|{settings.MicrophoneDevice}|{settings.MicrophoneDenoise}|" +
            string.Join("|", settings.Apps.Select(app => app.Executable));
    }
}
