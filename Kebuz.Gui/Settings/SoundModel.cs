using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
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

public sealed class AudioTestSourceRow : INotifyPropertyChanged
{
    private readonly Action<AudioTestSourceRow> picked;
    private bool solo;

    public AudioTestSourceRow(AudioTestSource source, Action<AudioTestSourceRow> picked)
    {
        Source = source;
        this.picked = picked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AudioTestSource Source { get; }

    public string Id => Source.Id;

    public string Title => Source.Title;

    public string Text => Strings.Format("Settings.Sound.TestSolo", Title);

    public bool Solo
    {
        get => solo;
        set
        {
            if (solo == value)
            {
                return;
            }

            solo = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Solo)));
        }
    }

    public void Pick()
    {
        picked(this);
    }

    public override string ToString()
    {
        return Title;
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
    private bool gameSourceAvailable = true;
    private bool manualSource;

    public bool GameSourceAvailable => gameSourceAvailable;
    public string GameSourceTitle => Strings.Get(manualSource ? "Recording.ApplicationAudio" : "Settings.Sound.GameSound");

    public void SetGameSource(bool available)
    {
        manualSource = true;
        gameSourceAvailable = available;
        Raise(nameof(GameSourceAvailable));
        Raise(nameof(GameSourceTitle));
        SyncTest();
    }
    private bool microphone = AudioSettings.Default.Microphone;
    private bool noiseSuppression = AudioSettings.Default.NoiseSuppression;
    private string? complaint;
    private bool syncing;
    private bool perProcess = true;
    private bool previewing;
    private AudioTestTake? testTake;
    private CancellationTokenSource? testCancellation;
    private SoundPlayer? testPlayer;
    private MemoryStream? testWave;
    private uint? testGameProcessId;
    private bool testBusy;
    private bool testPlaying;
    private string? testTrouble;
    private string? soloSource;

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

    public ObservableCollection<AudioTestSourceRow> TestSources { get; } = [];

    public bool PerProcessSupported => perProcess;

    public bool PerProcessBlocked => !perProcess;

    public string PerProcessNote => AudioDevices.PerProcessRequirement;

    public LevelMeter DesktopMeter { get; } = new();

    public LevelMeter GameMeter { get; } = new();

    public LevelMeter MicrophoneMeter { get; } = new();

    public LevelMeter MixMeter { get; } = new();

    public bool Previewing => previewing;

    public bool TestBusy => testBusy;

    public bool TestReady => testTake is not null;

    public bool TestPlaying => testPlaying;

    public uint? TestGameProcessId => testGameProcessId;

    public bool CanCaptureTest => !testBusy;

    public bool CanPlayTest => !testBusy && testTake is { HasSignal: true };

    public bool HasTestTrouble => testTrouble is not null;

    public string TestTroubleText => Strings.Render(testTrouble);

    public string TestCaptureText => Strings.Get(testTake is null
        ? "Settings.Sound.TestCapture"
        : "Settings.Sound.TestRecapture");

    public string TestPlayText => Strings.Get(testPlaying
        ? "Settings.Sound.TestStop"
        : "Settings.Sound.TestPlay");

    public string TestStatusText => Strings.Get(testBusy
        ? "Settings.Sound.TestRecording"
        : testTake is null
            ? "Settings.Sound.TestIdle"
            : testTake.HasSignal
                ? "Settings.Sound.TestReady"
                : "Settings.Sound.TestSilent");

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

        SyncTest();
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

    public async Task CaptureTest()
    {
        if (testBusy)
        {
            return;
        }

        StopTestPlayback();
        testCancellation?.Cancel();
        testCancellation?.Dispose();
        testCancellation = new CancellationTokenSource();
        testBusy = true;
        testTrouble = null;
        DropTestTake();
        RaiseTest();

        try
        {
            var settings = TestSettings();
            var gameProcessId = testGameProcessId;
            var take = await AudioMixTest.Capture(settings, gameProcessId, testCancellation.Token);

            if (!take.Matches(TestSettings(), testGameProcessId))
            {
                testTrouble = Strings.Get("Settings.Sound.TestChanged");
                return;
            }

            testTake = take;
            TestSources.Clear();

            foreach (var source in take.Sources)
            {
                TestSources.Add(new AudioTestSourceRow(source, ToggleSolo));
            }

            var unavailable = take.Sources.Where(source => !source.Live).Select(source => source.Title).ToList();

            if (settings.Mode is SystemAudioMode.Apps && settings.GameSound && gameProcessId is null)
            {
                unavailable.Insert(0, Strings.Get("Settings.Sound.GameSound"));
            }

            if (unavailable.Count > 0)
            {
                testTrouble = Strings.Format("Settings.Sound.TestUnavailable", string.Join(", ", unavailable));
            }

            if (take.HasSignal)
            {
                StartTestPlayback();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            testTrouble = ex.Message;
            Log.Error("проверка микса не удалась", ex);
        }
        finally
        {
            testBusy = false;
            RaiseTest();
        }
    }

    public void ToggleTestPlayback()
    {
        if (testPlaying)
        {
            StopTestPlayback();
        }
        else
        {
            StartTestPlayback();
        }

        RaiseTest();
    }

    public void DeactivateTest()
    {
        testCancellation?.Cancel();
        StopTestPlayback();
        RaiseTest();
    }

    public void SetTestGameProcess(uint? processId)
    {
        if (testGameProcessId == processId)
        {
            return;
        }

        testGameProcessId = processId;
        SyncTest();
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
        SyncTest();
    }

    private void SyncTest()
    {
        if (testTake is null)
        {
            return;
        }

        if (!testTake.Matches(TestSettings(), testGameProcessId))
        {
            StopTestPlayback();
            DropTestTake();
            testTrouble = null;
            RaiseTest();
            return;
        }

        if (testPlaying)
        {
            StartTestPlayback();
            RaiseTest();
        }
    }

    private void ToggleSolo(AudioTestSourceRow picked)
    {
        soloSource = picked.Solo ? null : picked.Id;

        foreach (var source in TestSources)
        {
            source.Solo = source.Id == soloSource;
        }

        if (testPlaying)
        {
            StartTestPlayback();
        }

        RaiseTest();
    }

    private void StartTestPlayback()
    {
        if (testTake is not { HasSignal: true })
        {
            return;
        }

        StopTestPlayback();

        try
        {
            testWave = testTake.Render(TestSettings(), soloSource);
            testPlayer = new SoundPlayer(testWave);
            testPlayer.Load();
            testPlayer.PlayLooping();
            testPlaying = true;
        }
        catch (Exception ex)
        {
            StopTestPlayback();
            testTrouble = ex.Message;
            Log.Error("проверка микса не воспроизводится", ex);
        }
    }

    private void StopTestPlayback()
    {
        testPlayer?.Stop();
        testPlayer?.Dispose();
        testPlayer = null;
        testWave?.Dispose();
        testWave = null;
        testPlaying = false;
    }

    private void DropTestTake()
    {
        testTake = null;
        soloSource = null;
        TestSources.Clear();
    }

    private AudioSettings TestSettings()
    {
        var settings = Current();
        return gameSourceAvailable ? settings : settings with { GameSound = false };
    }

    private void RaiseTest()
    {
        Raise(nameof(TestBusy));
        Raise(nameof(TestReady));
        Raise(nameof(TestPlaying));
        Raise(nameof(CanCaptureTest));
        Raise(nameof(CanPlayTest));
        Raise(nameof(HasTestTrouble));
        Raise(nameof(TestTroubleText));
        Raise(nameof(TestCaptureText));
        Raise(nameof(TestPlayText));
        Raise(nameof(TestStatusText));
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
        DeactivateTest();
        DropTestTake();
        Refresh();
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
