using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Settings;

public sealed class ProfileModel : INotifyPropertyChanged, ISection
{
    private readonly WatchedAppsModel games;
    private readonly WatchedAppRow row;
    private readonly Action close;

    private KebuzSettings common;
    private bool ownQuality;
    private bool ownBuffer;
    private bool ownSound;
    private bool ownHotkey;
    private bool loading;

    public ProfileModel(WatchedAppsModel games, WatchedAppRow row, KebuzSettings common, Action close, SampleModel? sample = null)
    {
        this.games = games;
        this.row = row;
        this.common = common;
        this.close = close;

        Quality = new QualityModel(() => Effective, Touch, Commit) { Sample = sample };
        Buffer = new BufferModel(() => Effective, Touch, Commit);
        Sound = new SoundModel(Commit);
        Hotkey = new HotkeyModel(Commit)
        {
            IdleKey = "Settings.Profile.HotkeyIdle"
        };

        Loc.Track(this, static owner => ((ProfileModel)owner).Relocalize());
        Seed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public QualityModel Quality { get; }

    public BufferModel Buffer { get; }

    public SoundModel Sound { get; }

    public HotkeyModel Hotkey { get; }

    public WatchedAppsModel Games => games;

    public string Title => Strings.Format("Settings.Profile.Title", row.Title);

    public string TitleName => Strings.Format("Settings.Profile.TitleName", row.Title);

    public string SummaryText => Strings.Format("Settings.Profile.Summary", Compose());

    public KebuzSettings Effective => Compose().Apply(common);

    public bool OwnQuality
    {
        get => ownQuality;
        set => Own(ref ownQuality, value, nameof(OwnQuality));
    }

    public bool OwnBuffer
    {
        get => ownBuffer;
        set => Own(ref ownBuffer, value, nameof(OwnBuffer));
    }

    public bool OwnSound
    {
        get => ownSound;
        set => Own(ref ownSound, value, nameof(OwnSound));
    }

    public bool OwnHotkey
    {
        get => ownHotkey;
        set => Own(ref ownHotkey, value, nameof(OwnHotkey));
    }

    public void Close()
    {
        close();
    }

    public void Commit()
    {
        Touch();
        Save();
    }

    public void Follow(KebuzSettings next)
    {
        common = next;
        loading = true;

        if (!ownQuality)
        {
            Quality.Adopt(next.Quality, next.CustomHeight, next.CustomFrameRate, next.CustomBitrateKbps, next.Capture);
        }

        if (!ownBuffer)
        {
            Buffer.Adopt(next.BufferSeconds, next.MergeSeconds, next.MergeMaxSeconds);
        }

        if (!ownSound)
        {
            Sound.Adopt(next.Audio);
        }

        if (!ownHotkey)
        {
            Hotkey.Adopt(next.Hotkey);
        }

        loading = false;
        Touch();
    }

    private void Seed()
    {
        loading = true;

        var profile = row.App.Overrides;

        ownQuality = profile.Quality is not null;
        ownBuffer = profile.Buffer is not null;
        ownSound = profile.Audio is not null;
        ownHotkey = profile.Hotkey is not null;

        var quality = profile.Quality;

        Quality.Adopt(
            quality?.Preset ?? common.Quality,
            quality?.Height ?? common.CustomHeight,
            quality?.FrameRate ?? common.CustomFrameRate,
            quality?.BitrateKbps ?? common.CustomBitrateKbps,
            quality?.Capture ?? common.Capture);

        var buffer = profile.Buffer;

        Buffer.Adopt(
            buffer?.BufferSeconds ?? common.BufferSeconds,
            buffer?.MergeSeconds ?? common.MergeSeconds,
            buffer?.MergeMaxSeconds ?? common.MergeMaxSeconds);

        Sound.Adopt(profile.Audio ?? common.Audio);
        Hotkey.Adopt(profile.Hotkey ?? common.Hotkey);

        loading = false;
    }

    private AppProfile Compose()
    {
        return new AppProfile(
            ownQuality ? Quality.Override : null,
            ownBuffer ? Buffer.Override : null,
            ownSound ? Sound.Current() : null,
            ownHotkey ? Hotkey.Combination : null);
    }

    private void Own(ref bool field, bool value, string name)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Raise(name);
        Commit();
    }

    private void Touch()
    {
        Quality.Refresh();
        Buffer.Refresh();
        Raise(nameof(SummaryText));
    }

    private void Save()
    {
        if (loading)
        {
            return;
        }

        games.SetProfile(row, Compose());
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
