using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Settings;

public sealed record AudioDeviceRow(string? Id, string Name)
{
    public string Label => Id is null ? Strings.Get("Settings.Sound.SystemDefault") : Name;

    public override string ToString()
    {
        return Label;
    }
}

public sealed class AudioAppRow : INotifyPropertyChanged
{
    private readonly Action changed;

    private bool enabled;
    private double level;

    public AudioAppRow(string executable, bool enabled, double level, Action changed)
    {
        Executable = executable;
        this.enabled = enabled;
        this.level = level;
        this.changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Executable { get; }

    public LevelMeter Meter { get; } = new();

    public int MaxLevel => AudioSettings.MaxLevel;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }

            enabled = value;
            Raise();
            changed();
        }
    }

    public double Level
    {
        get => level;
        set
        {
            if (Math.Abs(level - value) < 0.5)
            {
                return;
            }

            level = value;
            Raise();
            Raise(nameof(LevelText));
        }
    }

    public string LevelText => string.Create(Strings.Culture, $"{level:F0}");

    public override string ToString()
    {
        return Executable;
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class SoundModel : INotifyPropertyChanged, ISection
{
    private static readonly AudioDeviceRow SystemDefault = new(null, string.Empty);

    private readonly SynchronizationContext? context;
    private readonly Action push;

    private SystemAudioMode mode = AudioSettings.Default.Mode;
    private AudioDeviceRow output = SystemDefault;
    private AudioDeviceRow input = SystemDefault;
    private double desktopLevel = AudioSettings.Default.DesktopLevel;
    private double gameLevel = AudioSettings.Default.GameLevel;
    private double microphoneLevel = AudioSettings.Default.MicrophoneLevel;
    private bool gameSound = AudioSettings.Default.GameSound;
    private bool microphone = AudioSettings.Default.Microphone;
    private bool noiseSuppression = AudioSettings.Default.NoiseSuppression;
    private string? complaint;
    private bool syncing;
    private bool perProcess = true;
    private bool previewing;

    public SoundModel(Action push)
    {
        this.push = push;
        context = SynchronizationContext.Current;

        Outputs = [SystemDefault];
        Inputs = [SystemDefault];
        Loc.Track(this, static owner => ((SoundModel)owner).Relocalize());

        Task.Run(() => Settle(AudioDevices.PerProcessSupported));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioDeviceRow> Outputs { get; }

    public ObservableCollection<AudioDeviceRow> Inputs { get; }

    public ObservableCollection<AudioAppRow> Apps { get; } = [];

    public bool PerProcessSupported => perProcess;

    public bool PerProcessBlocked => !perProcess;

    public string PerProcessNote => AudioDevices.PerProcessRequirement;

    public LevelMeter DesktopMeter { get; } = new();

    public LevelMeter GameMeter { get; } = new();

    public LevelMeter MicrophoneMeter { get; } = new();

    public LevelMeter MixMeter { get; } = new();

    public bool Previewing => previewing;

    public int MaxLevel => AudioSettings.MaxLevel;

    public bool IsWholeDesktop
    {
        get => mode is SystemAudioMode.Desktop;
        set => Mode(SystemAudioMode.Desktop, value);
    }

    public bool IsByApps
    {
        get => mode is SystemAudioMode.Apps;
        set => Mode(SystemAudioMode.Apps, value);
    }

    public AudioDeviceRow Output
    {
        get => output;
        set
        {
            if (value is null || output == value)
            {
                return;
            }

            output = value;
            Raise();
            Send();
        }
    }

    public AudioDeviceRow Input
    {
        get => input;
        set
        {
            if (value is null || input == value)
            {
                return;
            }

            input = value;
            Raise();
            Send();
            Examine();
        }
    }

    public double DesktopLevel
    {
        get => desktopLevel;
        set => Level(ref desktopLevel, value, nameof(DesktopLevel), nameof(DesktopLevelText));
    }

    public double GameLevel
    {
        get => gameLevel;
        set => Level(ref gameLevel, value, nameof(GameLevel), nameof(GameLevelText));
    }

    public double MicrophoneLevel
    {
        get => microphoneLevel;
        set => Level(ref microphoneLevel, value, nameof(MicrophoneLevel), nameof(MicrophoneLevelText));
    }

    public string DesktopLevelText => string.Create(Strings.Culture, $"{desktopLevel:F0}");

    public string GameLevelText => string.Create(Strings.Culture, $"{gameLevel:F0}");

    public string MicrophoneLevelText => string.Create(Strings.Culture, $"{microphoneLevel:F0}");

    public bool GameSound
    {
        get => gameSound;
        set
        {
            if (gameSound == value)
            {
                return;
            }

            gameSound = value;
            Raise();
            Send();
        }
    }

    public bool Microphone
    {
        get => microphone;
        set
        {
            if (microphone == value)
            {
                return;
            }

            microphone = value;
            Raise();
            Send();

            if (microphone)
            {
                Examine();
            }
            else
            {
                Complain(null);
            }
        }
    }

    public bool NoiseSuppression
    {
        get => noiseSuppression;
        set
        {
            if (noiseSuppression == value)
            {
                return;
            }

            noiseSuppression = value;
            Raise();
            Send();
        }
    }

    public bool HasTrouble => microphone && complaint is not null;

    public string TroubleText => Strings.Render(complaint);

    public AudioSettings Current()
    {
        return new AudioSettings(
            mode,
            output.Id,
            (int)Math.Round(desktopLevel),
            gameSound,
            (int)Math.Round(gameLevel),
            Apps.Select(app => new AudioAppSetting(app.Executable, app.Enabled, (int)Math.Round(app.Level))).ToArray(),
            microphone,
            input.Id,
            (int)Math.Round(microphoneLevel),
            noiseSuppression);
    }

    public void Adopt(AudioSettings settings)
    {
        syncing = true;

        mode = settings.Mode;
        desktopLevel = settings.DesktopLevel;
        gameSound = settings.GameSound;
        gameLevel = settings.GameLevel;
        microphone = settings.Microphone;
        microphoneLevel = settings.MicrophoneLevel;
        noiseSuppression = settings.NoiseSuppression;

        Apps.Clear();

        foreach (var app in settings.Apps)
        {
            Apps.Add(new AudioAppRow(app.Executable, app.Enabled, app.Level, Send));
        }

        Refresh();

        output = Pick(Outputs, settings.DesktopDevice);
        input = Pick(Inputs, settings.MicrophoneDevice);

        syncing = false;

        Raise(nameof(Output));
        Raise(nameof(Input));

        Raise(nameof(IsWholeDesktop));
        Raise(nameof(IsByApps));
        Raise(nameof(DesktopLevel));
        Raise(nameof(DesktopLevelText));
        Raise(nameof(GameSound));
        Raise(nameof(GameLevel));
        Raise(nameof(GameLevelText));
        Raise(nameof(Microphone));
        Raise(nameof(MicrophoneLevel));
        Raise(nameof(MicrophoneLevelText));
        Raise(nameof(NoiseSuppression));

        if (microphone)
        {
            Examine();
        }
    }

    public void Refresh()
    {
        var quiet = syncing;
        syncing = true;

        Fill(Outputs, AudioDevices.Outputs(), ref output);
        Fill(Inputs, AudioDevices.Inputs(), ref input);

        syncing = quiet;

        Raise(nameof(Output));
        Raise(nameof(Input));
    }

    public void Add(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)
            || Apps.Any(app => app.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Apps.Add(new AudioAppRow(executable, true, AudioSettings.FullLevel, Send));
        Send();
    }

    public void Remove(AudioAppRow row)
    {
        if (Apps.Remove(row))
        {
            Send();
        }
    }

    public void Note(string? trouble)
    {
        Complain(trouble);
    }

    public void Commit()
    {
        Send();
    }

    public void Meter(AudioLevels levels)
    {
        Feed(DesktopMeter, levels, "desktop", null, desktopLevel);
        Feed(GameMeter, levels, "game", null, gameLevel);
        Feed(MicrophoneMeter, levels, "mic", null, microphoneLevel);

        foreach (var app in Apps)
        {
            Feed(app.Meter, levels, "process", app.Executable, app.Level);
        }

        if (levels.Sources.Count is 0)
        {
            MixMeter.Clear();
        }
        else
        {
            MixMeter.Show(levels.MixPeak, levels.MixRms, levels.Limited);
        }

        var next = levels.Preview && levels.Sources.Count > 0;

        if (previewing != next)
        {
            previewing = next;
            Raise(nameof(Previewing));
        }
    }

    private static void Feed(LevelMeter meter, AudioLevels levels, string kind, string? title, double level)
    {
        var source = levels.Sources.FirstOrDefault(candidate =>
            candidate.Kind == kind
            && (title is null || string.Equals(candidate.Title, title, StringComparison.OrdinalIgnoreCase)));

        if (source is null)
        {
            meter.Clear();
            return;
        }

        var gain = AudioGain.Of(level);
        meter.Show(source.Peak * gain, source.Rms * gain);
    }

    private void Examine()
    {
        if (!microphone)
        {
            return;
        }

        var device = input.Id;

        Task.Run(() =>
        {
            string? trouble;

            try
            {
                trouble = AudioDevices.TryOpenMicrophone(device);
            }
            catch (Exception ex)
            {
                trouble = ex.Message;
                Log.Error("проверка микрофона не удалась", ex);
            }

            if (context is null)
            {
                Complain(trouble);
                return;
            }

            context.Post(_ => Complain(trouble), null);
        });
    }

    private void Settle(bool supported)
    {
        if (context is null)
        {
            Accept(supported);
            return;
        }

        context.Post(_ => Accept(supported), null);
    }

    private void Accept(bool supported)
    {
        if (perProcess == supported)
        {
            return;
        }

        perProcess = supported;
        Raise(nameof(PerProcessSupported));
        Raise(nameof(PerProcessBlocked));

        if (!supported && mode is SystemAudioMode.Apps)
        {
            mode = SystemAudioMode.Desktop;
            Raise(nameof(IsWholeDesktop));
            Raise(nameof(IsByApps));
            Send();
        }
    }

    private void Complain(string? trouble)
    {
        if (complaint == trouble)
        {
            return;
        }

        complaint = trouble;
        Raise(nameof(HasTrouble));
        Raise(nameof(TroubleText));
    }

    private void Mode(SystemAudioMode next, bool value)
    {
        if (!value || mode == next)
        {
            return;
        }

        mode = next;
        Raise(nameof(IsWholeDesktop));
        Raise(nameof(IsByApps));
        Send();
    }

    private void Level(ref double field, double value, string name, string text)
    {
        if (Math.Abs(field - value) < 0.5)
        {
            return;
        }

        field = value;
        Raise(name);
        Raise(text);
    }

    private void Send()
    {
        if (syncing)
        {
            return;
        }

        push();
    }

    private static AudioDeviceRow Pick(IReadOnlyList<AudioDeviceRow> rows, string? id)
    {
        return rows.FirstOrDefault(row => row.Id == id) ?? rows[0];
    }

    private static void Fill(
        ObservableCollection<AudioDeviceRow> rows,
        IReadOnlyList<AudioDevice> devices,
        ref AudioDeviceRow chosen)
    {
        var wanted = chosen.Id;

        rows.Clear();
        rows.Add(SystemDefault);

        foreach (var device in devices)
        {
            rows.Add(new AudioDeviceRow(device.Id, device.Default ? Strings.Format("Settings.Sound.DeviceDefault", device.Name) : device.Name));
        }

        chosen = Pick(rows, wanted);
    }

    private void Relocalize()
    {
        Refresh();
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
