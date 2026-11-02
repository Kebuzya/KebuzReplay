using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Gui.Settings;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Gallery;

public sealed record CaptureSourceRow(CaptureSource Source)
{
    public string Title => Source.Kind == CaptureSourceKind.Monitor
        ? Strings.Format("Recording.MonitorLabel", new string(Source.Application.Where(char.IsDigit).ToArray()), Source.Title, Source.Width, Source.Height,
            Source.Primary ? Strings.Get("Recording.Primary") : string.Empty)
        : Strings.Format("Recording.WindowLabel", Source.Title, Source.Application);
    public override string ToString() => Title;
}

public sealed class RecordingPanelModel : INotifyPropertyChanged
{
    private readonly Action<KebuzSettings> save;
    private readonly Action<RecordingCommand> send;
    private KebuzSettings settings = KebuzSettings.Default;
    private RecordingControlState state = new(false, false, false, "idle");
    private bool loading;
    private bool configuring;
    private IReadOnlyList<CaptureSourceRow> sources = [];
    private IReadOnlyList<RecoveryFile> recoveries = [];
    private string? localError;
    private string? hotkeyError;

    public RecordingPanelModel(Action<KebuzSettings> save, Action<RecordingCommand> send)
    {
        this.save = save;
        this.send = send;
        Quality = new QualityModel(Effective, Refresh, Commit) { AllowCaptureMode = false };
        Buffer = new BufferModel(Effective, Refresh, Commit);
        Sound = new SoundModel(Commit);
        ControlHotkey = new HotkeyModel(Commit) { IdleKey = "Recording.HotkeyHelp", LabelKey = "Recording.ControlAction" };
        Loc.Track(this, static owner => ((RecordingPanelModel)owner).Refresh());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public QualityModel Quality { get; }
    public BufferModel Buffer { get; }
    public SoundModel Sound { get; }
    public HotkeyModel ControlHotkey { get; }
    public IReadOnlyList<CaptureSourceRow> Sources => sources;
    public IReadOnlyList<RecoveryFile> Recoveries => recoveries;
    public bool HasRecoveries => recoveries.Count > 0;
    public RecoveryFile? SelectedRecovery { get; set; }
    public bool CanChange => !state.Active && state.Stage == "idle";
    public bool CanStartStop => state.Stage is "idle" or "recording";
    public bool CanSaveReplay => state.Active && !state.Continuous;
    public bool StopConfirmation => state.StopConfirmation;
    public bool ShowSettings => configuring;
    public bool IsAutomatic
    {
        get => !settings.ManualControl;
        set { if (value) ChooseMode(false); }
    }
    public bool IsManual
    {
        get => settings.ManualControl;
        set { if (value) ChooseMode(true); }
    }
    public bool IsBuffer
    {
        get => Manual.Method == RecordingMethod.Buffer;
        set { if (value) ChooseMethod(RecordingMethod.Buffer); }
    }
    public bool IsContinuous
    {
        get => Manual.Method == RecordingMethod.Continuous;
        set { if (value) ChooseMethod(RecordingMethod.Continuous); }
    }
    public CaptureSourceRow? SelectedSource
    {
        get => sources.FirstOrDefault(row => row.Source.Id == Manual.SourceId);
        set
        {
            if (loading || !CanChange || value is null || value.Source.Id == Manual.SourceId) return;
            Change(settings with { Manual = Manual with { SourceId = value.Source.Id } });
        }
    }
    public string ButtonText => Strings.Get(state.Active
        ? state.Continuous ? "Recording.StopSave" : "Recording.DisableBuffer"
        : IsManual ? IsContinuous ? "Recording.Start" : "Recording.EnableBuffer" : "Recording.ResumeAuto");
    public string StatusText => localError ?? Strings.Render(state.Error) switch
    {
        { Length: > 0 } error => error,
        _ => state.Stage is "starting" or "finalizing" ? Strings.Get("Recording.Stage." + state.Stage)
            : state.Active && state.Continuous
                ? Strings.Format("Recording.Progress", TimeSpan.FromSeconds(state.Seconds).ToString(@"hh\:mm\:ss"),
                    Kebuz.Recorder.Diagnostics.Sizes.Format(state.Bytes))
                : Strings.Get(state.Active ? "Recording.BufferActive" : IsManual ? "Recording.WaitManual" : "Recording.WaitGame")
    };
    public string MethodHelp => Strings.Get(IsContinuous ? "Recording.ContinuousHelp" : "Recording.BufferHelp");
    public string Estimate => IsContinuous
        ? Strings.Format("Recording.DiskEstimate", Kebuz.Recorder.Diagnostics.Sizes.Format(
            Formats.ClipBytes(Effective().Profile, 60, Effective().Audio)))
        : Strings.Format("Recording.MemoryEstimate", Kebuz.Recorder.Diagnostics.Sizes.Format(
            Formats.BufferBytes(Effective().Profile, Buffer.BufferSeconds, Buffer.MergeMaxSeconds, Effective().Audio)));
    public string HotkeyComplaint => hotkeyError ?? string.Empty;
    public bool HasHotkeyComplaint => !string.IsNullOrEmpty(hotkeyError);
    private ManualRecordingSettings Manual => settings.Manual ?? ManualRecordingSettings.From(settings);

    private KebuzSettings Effective()
    {
        return Manual.Apply(settings) with
        {
            Quality = Quality.Preset, CustomHeight = Quality.Height, CustomFrameRate = Quality.FrameRate,
            CustomBitrateKbps = Quality.Kbps, BufferSeconds = Buffer.BufferSeconds,
            MergeSeconds = Buffer.MergeSeconds, MergeMaxSeconds = Buffer.MergeMaxSeconds, Audio = Sound.Current()
        };
    }

    public void Adopt(KebuzSettings next)
    {
        loading = true;
        if (settings.ControlHotkey != next.ControlHotkey) hotkeyError = null;
        settings = next;
        var manual = Manual;
        Quality.Adopt(manual.Quality, manual.Height, manual.FrameRate, manual.BitrateKbps, CaptureMode.Standard);
        Buffer.Adopt(manual.BufferSeconds, manual.MergeSeconds, manual.MergeMaxSeconds);
        Sound.Adopt(manual.Audio);
        ControlHotkey.Adopt(next.ControlHotkey ?? string.Empty);
        Sound.SetGameSource(SelectedSource?.Source.Kind == CaptureSourceKind.Window);
        loading = false;
        Refresh();
    }

    public void SetState(RecordingControlState next)
    {
        state = next;
        localError = null;
        if (next.Recoveries is not null)
        {
            recoveries = next.Recoveries;
            SelectedRecovery = recoveries.FirstOrDefault();
        }
        Refresh();
    }

    public void SetSources(CaptureSourceList next)
    {
        sources = next.Sources.Select(source => new CaptureSourceRow(source)).ToArray();
        localError = Strings.Render(next.Error);
        if (string.IsNullOrEmpty(localError)) localError = null;
        Sound.SetGameSource(SelectedSource?.Source.Kind == CaptureSourceKind.Window);
        Refresh();
    }

    public void NoteHotkey(HotkeyState next)
    {
        hotkeyError = next.Registered ? null : Strings.Format("Hotkey.NotWorking", next.Combination, Strings.Render(next.Complaint));
        Refresh();
    }

    public void RefreshSources() => send(new RecordingCommand("sources"));
    public void Toggle()
    {
        if (!CanStartStop) return;
        var active = state.Active;
        state = state with { Stage = active ? "recording" : "starting" };
        send(new RecordingCommand(active ? "requestStop" : "start"));
        Refresh();
    }
    public void Stop(bool saveBuffer) => send(new RecordingCommand("stop", saveBuffer));
    public void CancelStop() => send(new RecordingCommand("cancelStop"));
    public void ToggleSettings() { configuring = !configuring; Refresh(); }
    public void Recover()
    {
        if (CanChange && SelectedRecovery is { } item) send(new RecordingCommand("recover", RecoveryId: item.Id));
    }
    public void ClearHotkey()
    {
        ControlHotkey.Adopt(string.Empty);
        Commit();
    }

    private void ChooseMode(bool manual)
    {
        if (loading || !CanChange || settings.ManualControl == manual) return;
        Change(settings with { ManualControl = manual, Manual = Manual });
    }
    private void ChooseMethod(RecordingMethod method)
    {
        if (loading || !CanChange || Manual.Method == method) return;
        Change(settings with { Manual = Manual with { Method = method } });
    }
    private void Commit()
    {
        if (loading) return;
        var current = Effective();
        Change(settings with
        {
            ControlHotkey = string.IsNullOrWhiteSpace(ControlHotkey.Combination) ? null : ControlHotkey.Combination,
            Manual = Manual with
            {
                Quality = current.Quality, Height = current.CustomHeight, FrameRate = current.CustomFrameRate,
                BitrateKbps = current.CustomBitrateKbps, BufferSeconds = current.BufferSeconds,
                MergeSeconds = current.MergeSeconds, MergeMaxSeconds = current.MergeMaxSeconds, Audio = current.Audio
            }
        });
    }
    private void Change(KebuzSettings next)
    {
        settings = next;
        Sound.SetGameSource(SelectedSource?.Source.Kind == CaptureSourceKind.Window);
        save(next);
        Refresh();
    }
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

internal static class RecordingUiState
{
    public static void Apply(RecordingControlState next, RecordingPanelModel panel, GalleryModel gallery, SettingsModel settings)
    {
        panel.SetState(next);
        if (!next.Active)
        {
            var idle = new RecordingState(false, null, null);
            gallery.SetRecording(idle);
            settings.SetRecording(idle);
        }
        settings.Sample.SetRecording(next.Active || next.Stage is "starting" or "finalizing");
    }
}
