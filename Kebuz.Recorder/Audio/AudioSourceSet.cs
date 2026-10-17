using Kebuz.Recorder.Settings;
using Kebuz.Recorder.Watching;
using Windows.Win32.Media.Audio;

namespace Kebuz.Recorder.Audio;

internal sealed record AudioAppTarget(string Executable, int Level);

internal sealed record AudioTrackSettings(
    bool Desktop,
    string? DesktopDevice,
    int DesktopLevel,
    bool GameSound,
    int GameLevel,
    IReadOnlyList<AudioAppTarget> Apps,
    bool Microphone,
    string? MicrophoneDevice,
    int MicrophoneLevel,
    bool MicrophoneDenoise = false)
{
    public bool AnySource => Desktop || GameSound || Apps.Count > 0 || Microphone;

    public static AudioTrackSettings Of(AudioSettings settings)
    {
        var byApps = settings.Mode is SystemAudioMode.Apps;

        return new AudioTrackSettings(
            !byApps,
            settings.DesktopDevice,
            settings.DesktopLevel,
            byApps && settings.GameSound,
            settings.GameLevel,
            byApps
                ? settings.Apps
                    .Where(app => app.Enabled)
                    .Select(app => new AudioAppTarget(app.Executable, app.Level))
                    .ToArray()
                : [],
            settings.Microphone,
            settings.MicrophoneDevice,
            settings.MicrophoneLevel,
            settings.NoiseSuppression);
    }
}

internal sealed record AudioSourceSetRequest(
    AudioTrackSettings Settings,
    GameProcess? Game,
    Action<string> Trace);

internal sealed class GameProcess
{
    private long id;

    public GameProcess(uint? id)
    {
        Id = id;
    }

    public uint? Id
    {
        get => Interlocked.Read(ref id) is var value and > 0 ? (uint)value : null;
        set => Interlocked.Exchange(ref id, value ?? 0);
    }
}

internal static class AudioSourceSet
{
    public static List<AudioSource> Gather(AudioSourceSetRequest request)
    {
        var settings = request.Settings;
        var sources = new List<AudioSource>();

        if (settings.Desktop)
        {
            sources.Add(new DeviceAudioSource(
                AudioSourceKind.Desktop,
                EDataFlow.eRender,
                true,
                settings.DesktopDevice,
                "Audio.Desktop",
                request.Trace)
            {
                Level = settings.DesktopLevel
            });
        }

        if (settings.GameSound && request.Game is { Id: not null } game)
        {
            sources.Add(new ProcessAudioSource(() => game.Id, "Audio.Game", request.Trace)
            {
                Level = settings.GameLevel,
                Game = true
            });
        }

        foreach (var app in settings.Apps)
        {
            var executable = app.Executable;

            sources.Add(new ProcessAudioSource(() => Resolve(executable), executable, request.Trace)
            {
                Level = app.Level
            });
        }

        if (settings.Microphone)
        {
            sources.Add(new DeviceAudioSource(
                AudioSourceKind.Microphone,
                EDataFlow.eCapture,
                false,
                settings.MicrophoneDevice,
                "Audio.Microphone",
                request.Trace,
                settings.MicrophoneDenoise)
            {
                Level = settings.MicrophoneLevel
            });
        }

        return sources;
    }

    public static string Key(AudioSourceKind kind)
    {
        return kind switch
        {
            AudioSourceKind.Desktop => "desktop",
            AudioSourceKind.Process => "process",
            _ => "mic"
        };
    }

    public static string MeterKey(AudioSource source)
    {
        return source.Game ? "game" : Key(source.Kind);
    }

    public static uint? Resolve(string wanted)
    {
        if (uint.TryParse(wanted, out var direct))
        {
            return direct;
        }

        var running = new List<RunningProcess>();
        ProcessScanner.Snapshot(running);

        foreach (var process in running)
        {
            if (process.Executable.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return process.Id;
            }
        }

        return null;
    }
}
