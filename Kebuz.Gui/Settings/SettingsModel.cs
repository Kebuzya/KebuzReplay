using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Gui.Shell;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Settings;

public sealed class SettingsModel : INotifyPropertyChanged, ISection
{
    private readonly AppLink? link;

    private KebuzSettings applied = KebuzSettings.Default;
    private bool limitFolder = KebuzSettings.Default.LimitFolder;
    private double limitGigabytes = KebuzSettings.Default.LimitGigabytes;
    private bool deleteOld = KebuzSettings.Default.DeleteOld;
    private bool detectGames = KebuzSettings.Default.DetectGames;
    private bool keepAfterExit = KebuzSettings.Default.KeepAfterExit;
    private double keepAfterExitSeconds = KebuzSettings.Default.KeepAfterExitSeconds;
    private bool followLargerWindow = KebuzSettings.Default.FollowLargerWindow;
    private bool checkUpdates = KebuzSettings.Default.CheckUpdates;
    private string uiLanguage = KebuzSettings.Default.UiLanguage;
    private ProfileModel? profile;
    private long occupiedBytes;
    private long taggedBytes;
    private int clipCount;
    private bool lostBorderless;
    private bool applyClosed;

    public SettingsModel(AppLink? link)
    {
        this.link = link;

        Games = new WatchedAppsModel();
        Quality = new QualityModel(Compose, RaiseDerived, Commit) { Sample = Sample };
        Buffer = new BufferModel(Compose, RaiseDerived, Commit);
        Sound = new SoundModel(Commit);
        Hotkey = new HotkeyModel(Commit);

        Games.ProfileEdited += Reshow;
        Loc.Track(this, static owner => ((SettingsModel)owner).Relocalize());

    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<KebuzSettings>? Pushed;

    public event Action? RereadRequested;

    public bool CanApplyNow { get; private set; }

    public SampleModel Sample { get; } = new();

    public bool ShowApplyNotice => CanApplyNow && !applyClosed;

    public void SetRecording(RecordingState state)
    {
        lostBorderless = state.LostBorderless();
        Sample.SetRecording(state.Recording);

        var next = state.Recording && state.SettingsPending;

        if (next == CanApplyNow)
        {
            return;
        }

        CanApplyNow = next;

        if (!next)
        {
            applyClosed = false;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanApplyNow)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowApplyNotice)));
    }

    public void CloseApplyNotice()
    {
        applyClosed = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowApplyNotice)));
    }

    public void ApplyNow()
    {
        if (!(CanApplyNow || lostBorderless) || link is null)
        {
            return;
        }

        link.Send(PipeMessages.ApplySettingsNow);
    }

    public QualityModel Quality { get; }

    public BufferModel Buffer { get; }

    public SoundModel Sound { get; }

    public HotkeyModel Hotkey { get; }

    public WatchedAppsModel Games { get; }

    public ProfileModel? Profile => profile;

    public bool IsProfileOpen => profile is not null;

    public bool IsCommonOpen => profile is null;

    public bool LimitFolder
    {
        get => limitFolder;
        set
        {
            if (limitFolder == value)
            {
                return;
            }

            limitFolder = value;

            if (!limitFolder && deleteOld)
            {
                deleteOld = false;
                Raise(nameof(DeleteOld));
            }

            Raise(nameof(LimitFolder));
            RaiseLimit();
            Push();
        }
    }

    public double LimitGigabytes
    {
        get => limitGigabytes;
        set
        {
            if (Math.Abs(limitGigabytes - value) < 0.5)
            {
                return;
            }

            limitGigabytes = value;
            Raise(nameof(LimitGigabytes));
            RaiseLimit();
        }
    }

    public bool DeleteOld
    {
        get => deleteOld;
        set
        {
            if (deleteOld == value)
            {
                return;
            }

            deleteOld = value;
            Raise(nameof(DeleteOld));
            RaiseLimit();
            Push();
        }
    }

    public bool DetectGames
    {
        get => detectGames;
        set
        {
            if (detectGames == value)
            {
                return;
            }

            detectGames = value;
            Raise(nameof(DetectGames));
            Push();
        }
    }

    public bool KeepAfterExit
    {
        get => keepAfterExit;
        set
        {
            if (keepAfterExit == value)
            {
                return;
            }

            keepAfterExit = value;
            Raise(nameof(KeepAfterExit));
            Push();
        }
    }

    public double KeepAfterExitSeconds
    {
        get => keepAfterExitSeconds;
        set
        {
            if (Math.Abs(keepAfterExitSeconds - value) < 0.5)
            {
                return;
            }

            keepAfterExitSeconds = value;
            Raise(nameof(KeepAfterExitSeconds));
            Raise(nameof(KeepAfterExitText));
        }
    }

    public bool FollowLargerWindow
    {
        get => followLargerWindow;
        set
        {
            if (followLargerWindow == value)
            {
                return;
            }

            followLargerWindow = value;
            Raise(nameof(FollowLargerWindow));
            Push();
        }
    }

    public bool CheckUpdates
    {
        get => checkUpdates;
        set
        {
            if (checkUpdates == value)
            {
                return;
            }

            checkUpdates = value;
            Raise(nameof(CheckUpdates));
            Push();
        }
    }

    public bool IsLanguageSystem
    {
        get => uiLanguage == Strings.System;
        set => PickLanguage(Strings.System, value);
    }

    public bool IsLanguageRussian
    {
        get => uiLanguage == Strings.RussianCode;
        set => PickLanguage(Strings.RussianCode, value);
    }

    public bool IsLanguageEnglish
    {
        get => uiLanguage == Strings.EnglishCode;
        set => PickLanguage(Strings.EnglishCode, value);
    }

    public string VersionText => $"KebuzReplay {Shell.UpdateCheck.CurrentText}";

    public string RepositoryText => Shell.UpdateCheck.Page;

    public void SkipVersion(string version)
    {
        Send(Compose() with { SkippedVersion = version });
    }

    public string KeepAfterExitText => Sizes.Length(TimeSpan.FromSeconds(keepAfterExitSeconds));

    public string ClipWeightText =>
        Strings.Format("Settings.Storage.ClipWeight", Sizes.Format(ClipBytes(Buffer.Seconds)));

    public string OccupiedText => Strings.Format("Settings.Storage.Occupied", Sizes.Format(occupiedBytes), clipCount);

    public string LimitText => Strings.Format("Settings.Storage.LimitValue", limitGigabytes);

    public bool HasWarning => limitFolder && occupiedBytes > LimitBytes;

    public string WarningText
    {
        get
        {
            if (!HasWarning)
            {
                return string.Empty;
            }

            var head = Strings.Format("Settings.Storage.OverLimit", Sizes.Format(occupiedBytes), LimitText);

            if (!deleteOld)
            {
                return Strings.Format("Settings.Storage.OverLimitOff", head);
            }

            if (taggedBytes > 0 && taggedBytes > Math.Max(0, LimitBytes - ClipBytes(Buffer.Seconds)))
            {
                return Strings.Format("Settings.Storage.OverLimitTagged", head, Sizes.Format(taggedBytes));
            }

            return Strings.Format("Settings.Storage.OverLimitStuck", head);
        }
    }

    public string ClipsFolder => applied.EffectiveClipsRoot;

    public KebuzSettings Effective => Compose();

    private long LimitBytes => Formats.LimitBytes(limitGigabytes);

    public void OpenProfile(WatchedAppRow row)
    {
        profile = new ProfileModel(Games, row, Compose(), CloseProfile, Sample);
        RaiseProfile();
    }

    public void CloseProfile()
    {
        if (profile is null)
        {
            return;
        }

        profile = null;
        RaiseProfile();
    }

    public void Occupied(long bytes, int count, long tagged)
    {
        occupiedBytes = bytes;
        clipCount = count;
        taggedBytes = tagged;
        Raise(nameof(OccupiedText));
        RaiseLimit();
    }

    public void Adopt(KebuzSettings next)
    {
        applied = next;
        limitFolder = next.LimitFolder;
        limitGigabytes = next.LimitGigabytes;
        deleteOld = next.DeleteOld;
        detectGames = next.DetectGames;
        keepAfterExit = next.KeepAfterExit;
        keepAfterExitSeconds = next.KeepAfterExitSeconds;
        followLargerWindow = next.FollowLargerWindow;
        checkUpdates = next.CheckUpdates;
        uiLanguage = next.UiLanguage;

        Quality.Adopt(next.Quality, next.CustomHeight, next.CustomFrameRate, next.CustomBitrateKbps, next.Capture);
        Buffer.Adopt(next.BufferSeconds, next.MergeSeconds, next.MergeMaxSeconds);
        Hotkey.Adopt(next.Hotkey);
        Sound.Adopt(next.Audio);

        Raise(nameof(LimitFolder));
        Raise(nameof(LimitGigabytes));
        Raise(nameof(DeleteOld));
        Raise(nameof(DetectGames));
        Raise(nameof(KeepAfterExit));
        Raise(nameof(KeepAfterExitSeconds));
        Raise(nameof(KeepAfterExitText));
        Raise(nameof(FollowLargerWindow));
        Raise(nameof(CheckUpdates));
        RaiseLanguage();
        Raise(nameof(ClipsFolder));
        RaiseDerived();

        profile?.Follow(Compose());
    }

    public void Commit()
    {
        RaiseDerived();
        Push();
    }

    public void Push()
    {
        Send(Compose());
    }

    public void Relocate(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        if (string.Equals(root, applied.EffectiveClipsRoot, StringComparison.OrdinalIgnoreCase))
        {
            RereadRequested?.Invoke();
            return;
        }

        Send(applied with { ClipsRoot = root });
        Raise(nameof(ClipsFolder));
    }

    public long ClipBytes(double seconds)
    {
        var settings = Compose();

        return Formats.ClipBytes(settings.Profile, seconds, settings.Audio);
    }

    private KebuzSettings Compose()
    {
        return applied with
        {
            Quality = Quality.Preset,
            CustomHeight = Quality.Height,
            CustomFrameRate = Quality.FrameRate,
            CustomBitrateKbps = Quality.Kbps,
            Capture = Quality.Capture,
            BufferSeconds = Buffer.BufferSeconds,
            MergeSeconds = Buffer.MergeSeconds,
            MergeMaxSeconds = Buffer.MergeMaxSeconds,
            LimitFolder = limitFolder,
            LimitGigabytes = limitGigabytes,
            DeleteOld = deleteOld,
            DetectGames = detectGames,
            KeepAfterExit = keepAfterExit,
            KeepAfterExitSeconds = keepAfterExitSeconds,
            FollowLargerWindow = followLargerWindow,
            CheckUpdates = checkUpdates,
            UiLanguage = uiLanguage,
            Hotkey = Hotkey.Combination,
            Audio = Sound.Current()
        };
    }

    private void Send(KebuzSettings next)
    {
        if (next == applied)
        {
            return;
        }

        applied = next;
        Pushed?.Invoke(next);
        Reshow();

        if (link is null)
        {
            Log.Error("труба супервизора недоступна, настройки не отправлены");
            return;
        }

        link.Send(PipeMessages.UpdateSettings, next);
    }

    private void PickLanguage(string code, bool value)
    {
        if (!value || uiLanguage == code)
        {
            return;
        }

        uiLanguage = code;
        RaiseLanguage();
        Strings.Use(Strings.Resolve(code));
        Push();
    }

    private void RaiseLanguage()
    {
        Raise(nameof(IsLanguageSystem));
        Raise(nameof(IsLanguageRussian));
        Raise(nameof(IsLanguageEnglish));
    }

    private void Reshow()
    {
        if (!applyClosed)
        {
            return;
        }

        applyClosed = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowApplyNotice)));
    }

    private void RaiseProfile()
    {
        Raise(nameof(Profile));
        Raise(nameof(IsProfileOpen));
        Raise(nameof(IsCommonOpen));
    }

    private void RaiseDerived()
    {
        Quality.Refresh();
        Buffer.Refresh();
        Raise(nameof(ClipWeightText));
        RaiseLimit();
    }

    private void RaiseLimit()
    {
        Raise(nameof(LimitText));
        Raise(nameof(HasWarning));
        Raise(nameof(WarningText));
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
