using System.Text.Json.Serialization;

namespace Kebuz.Recorder.Settings;

public enum SystemAudioMode
{
    Desktop,
    Apps
}

public sealed record AudioAppSetting(string Executable, bool Enabled, int Level);

public sealed record AudioSettings(
    SystemAudioMode Mode,
    string? DesktopDevice,
    int DesktopLevel,
    bool GameSound,
    int GameLevel,
    IReadOnlyList<AudioAppSetting> Apps,
    bool Microphone,
    string? MicrophoneDevice,
    int MicrophoneLevel,
    bool NoiseSuppression = false)
{
    public const int FullLevel = 100;
    public const int MaxLevel = 200;

    public static AudioSettings Default { get; } = new(
        SystemAudioMode.Desktop,
        null,
        FullLevel,
        true,
        FullLevel,
        [],
        false,
        null,
        FullLevel);

    [JsonIgnore]
    public bool AnySource => Mode is SystemAudioMode.Desktop
        || GameSound
        || Apps.Any(app => app.Enabled)
        || Microphone;

    public bool Equals(AudioSettings? other)
    {
        return other is not null
            && Mode == other.Mode
            && DesktopDevice == other.DesktopDevice
            && DesktopLevel == other.DesktopLevel
            && GameSound == other.GameSound
            && GameLevel == other.GameLevel
            && Microphone == other.Microphone
            && MicrophoneDevice == other.MicrophoneDevice
            && MicrophoneLevel == other.MicrophoneLevel
            && NoiseSuppression == other.NoiseSuppression
            && Apps.SequenceEqual(other.Apps);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(Mode);
        hash.Add(DesktopDevice);
        hash.Add(DesktopLevel);
        hash.Add(GameSound);
        hash.Add(GameLevel);
        hash.Add(Microphone);
        hash.Add(MicrophoneDevice);
        hash.Add(MicrophoneLevel);
        hash.Add(NoiseSuppression);

        foreach (var app in Apps)
        {
            hash.Add(app);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var system = Mode is SystemAudioMode.Desktop
            ? Localization.Strings.Format("Sound.Desktop", DesktopLevel)
            : Localization.Strings.Format("Sound.Apps", string.Join(" ", Named()));

        return Microphone
            ? Localization.Strings.Format("Sound.WithMic", system, MicrophoneLevel, NoiseSuppression ? Localization.Strings.Get("Sound.Denoised") : string.Empty)
            : Localization.Strings.Format("Sound.NoMic", system);
    }

    public AudioSettings Fix(bool perProcessSupported)
    {
        var mode = perProcessSupported ? Mode : SystemAudioMode.Desktop;

        var apps = Apps
            .Where(app => !string.IsNullOrWhiteSpace(app.Executable))
            .Select(app => app with { Level = Math.Clamp(app.Level, 0, MaxLevel) })
            .ToArray();

        return this with
        {
            Mode = mode,
            DesktopLevel = Math.Clamp(DesktopLevel, 0, MaxLevel),
            GameLevel = Math.Clamp(GameLevel, 0, MaxLevel),
            MicrophoneLevel = Math.Clamp(MicrophoneLevel, 0, MaxLevel),
            Apps = apps
        };
    }

    private IEnumerable<string> Named()
    {
        if (GameSound)
        {
            yield return Localization.Strings.Format("Sound.Game", GameLevel);
        }

        foreach (var app in Apps.Where(app => app.Enabled))
        {
            yield return $"{app.Executable} {app.Level}";
        }
    }
}
