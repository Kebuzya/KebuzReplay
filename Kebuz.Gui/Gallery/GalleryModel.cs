using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Kebuz.Gui.Localization;
using Kebuz.Gui.Player;
using Kebuz.Gui.Thumbs;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Gallery;

public sealed partial class ClipItem : INotifyPropertyChanged
{
    private ImageSource? shot;
    private bool chosen;
    private bool anchor;

    public ClipItem(ClipEntry entry, string output, IReadOnlyList<TagRecord> tags)
    {
        Path = entry.Path;
        FileName = System.IO.Path.GetFileName(entry.Path);
        Game = entry.Game;
        Session = entry.Session;
        When = entry.When;
        Length = entry.Duration;
        SizeBytes = entry.SizeBytes;
        WithoutMicrophone = entry.WithoutMicrophone;
        Output = output;
        IsCut = CutName().IsMatch(System.IO.Path.GetFileNameWithoutExtension(entry.Path));

        foreach (var tag in tags)
        {
            Tags.Add(tag);
        }

        Loc.Track(this, static owner => ((ClipItem)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string FileName { get; }

    public string Game { get; }

    public string Session { get; }

    public DateTime When { get; }

    public TimeSpan Length { get; }

    public long SizeBytes { get; }

    public string Output { get; }

    public ObservableCollection<TagRecord> Tags { get; } = [];

    public ImageSource? Shot
    {
        get => shot;
        set
        {
            if (ReferenceEquals(shot, value))
            {
                return;
            }

            shot = value;
            Raise(nameof(Shot));
            Raise(nameof(HasShot));
        }
    }

    public bool HasShot => shot is not null;

    public double Middle => Length.TotalSeconds / 2;

    public bool IsChosen
    {
        get => chosen;
        set
        {
            if (chosen == value)
            {
                return;
            }

            chosen = value;
            Raise(nameof(IsChosen));
        }
    }

    public bool IsAnchor
    {
        get => anchor;
        set
        {
            if (anchor == value)
            {
                return;
            }

            anchor = value;
            Raise(nameof(IsAnchor));
        }
    }

    public string Duration => Sizes.Length(Length);

    public bool WithoutMicrophone { get; }

    public bool IsCut { get; }

    public string Notice => WithoutMicrophone
        ? Strings.Format("Gallery.Notice.SavedNoMic", Duration)
        : Strings.Format("Gallery.Notice.Saved", Duration);

    public double Seconds => Length.TotalSeconds;

    public string Time => When.ToString(Strings.Get("Gallery.TimeFormat"), Strings.Culture);

    public string SizeText => Sizes.Format(SizeBytes);

    public string HeadlineText => $"{Game} · {Time} · {Output}";

    public string DetailsText => $"{SizeText} · {Session}";

    public string AccessibleName => IsCut
        ? Strings.Format("Gallery.Accessible.Cut", Game, Duration, Time)
        : Strings.Format("Gallery.Accessible.Clip", Game, Duration, Time);

    public bool Matches(string text)
    {
        if (Game.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Session.Contains(text, StringComparison.OrdinalIgnoreCase)
            || FileName.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var tag in Tags)
        {
            if (tag.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Retag(IReadOnlyList<TagRecord> tags)
    {
        Tags.Clear();

        foreach (var tag in tags)
        {
            Tags.Add(tag);
        }

        Raise(nameof(Tags));
    }

    public override string ToString()
    {
        return AccessibleName;
    }

    [GeneratedRegex("_cut(_[0-9]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CutName();

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }
}

public sealed class TagChoice : INotifyPropertyChanged
{
    private bool? state;
    private bool confirming;
    private string warning = string.Empty;

    private TagRecord tag;

    public TagChoice(TagRecord tag)
    {
        this.tag = tag;
        Loc.Track(this, static owner => ((TagChoice)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TagRecord Tag
    {
        get => tag;
        set
        {
            tag = value;
            Raise(nameof(Tag));
            Raise(nameof(Label));
            Raise(nameof(Color));
            Raise(nameof(RemoveName));
            Raise(nameof(EditName));
        }
    }

    public string Label => Tag.Label;

    public string Color => Tag.Color;

    public string RemoveName => Strings.Format("Tags.RemoveName", Tag.Label);

    public string EditName => Strings.Format("Tags.EditName", Tag.Label);

    public bool? State
    {
        get => state;
        set
        {
            if (state == value)
            {
                return;
            }

            state = value;
            Raise(nameof(State));
        }
    }

    public bool IsConfirming
    {
        get => confirming;
        set
        {
            if (confirming == value)
            {
                return;
            }

            confirming = value;
            Raise(nameof(IsConfirming));
        }
    }

    public string Warning
    {
        get => warning;
        set
        {
            if (warning == value)
            {
                return;
            }

            warning = value;
            Raise(nameof(Warning));
        }
    }

    public override string ToString()
    {
        return Tag.Label;
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }
}

public sealed class SessionGroup : INotifyPropertyChanged
{
    public SessionGroup(string folder, DateTime started)
    {
        Folder = folder;
        Started = started;
        Clips.CollectionChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ClipCountText)));
        Loc.Track(this, static owner => ((SessionGroup)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Folder { get; }

    public DateTime Started { get; }

    public ObservableCollection<ClipItem> Clips { get; } = [];

    public string Title => Started.ToString(Strings.Get("Gallery.SessionDate"), Strings.Culture);

    public string ClipCountText => Strings.Format("Gallery.ClipCount", Clips.Count);

    public override string ToString()
    {
        return Strings.Format("Gallery.SessionTitle", Title);
    }

    private void Relocalize()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed class GameGroup : INotifyPropertyChanged
{
    private DateTime latest;
    private bool collapsed;

    public GameGroup(string title, DateTime latest)
    {
        Title = title;
        this.latest = latest;
        Loc.Track(this, static owner => ((GameGroup)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    public DateTime Latest => latest;

    public ObservableCollection<SessionGroup> Sessions { get; } = [];

    public string When => latest.ToString(Strings.Get("Gallery.GameDate"), Strings.Culture);

    public bool IsCollapsed
    {
        get => collapsed;
        set
        {
            if (collapsed == value)
            {
                return;
            }

            collapsed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FoldName)));
        }
    }

    public string FoldName => collapsed ? Strings.Format("Gallery.Expand", Title) : Strings.Format("Gallery.Collapse", Title);

    public void Note(DateTime when)
    {
        if (when <= latest)
        {
            return;
        }

        latest = when;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(When)));
    }

    public override string ToString()
    {
        return $"{Title}, {When}";
    }

    private void Relocalize()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed class GalleryModel : INotifyPropertyChanged, IDisposable
{
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private readonly Dictionary<string, ClipItem> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThumbnailQueue thumbs = new();
    private readonly List<ClipEntry> pending = [];
    private readonly HashSet<ClipItem> chosen = [];
    private readonly List<ClipItem> all = [];

    private KebuzSettings settings = KebuzSettings.Default;
    private ClipStore? store;
    private ClipItem? selected;
    private int generation;
    private bool loading;
    private string? root;
    private bool refreshing;
    private bool recording;
    private string? game;
    private bool profiled;
    private double bufferSeconds;
    private string? sessionHotkey;
    private string? captureNote;
    private bool settingsPending;
    private bool lostBorderless;
    private CaptureCard? recordingCard;
    private long memoryBytes;
    private string? restart;
    private string? recovery;
    private SourceHealth? sound;
    private HotkeyState? hotkey;
    private string search = string.Empty;
    private FilterOption gameFilter = GalleryFilters.AnyGame;
    private FilterOption tagFilter = GalleryFilters.AnyTag;
    private FilterOption dateFilter = GalleryFilters.Dates[0];
    private FilterOption order = GalleryFilters.Orders[0];
    private bool listing;
    private bool syncing;
    private int shown;
    private TagChoice? editing;
    private string draft = string.Empty;
    private bool removing;
    private bool lonely;
    private string? trouble;
    private bool cutting;

    public GalleryModel()
    {
        foreach (var icon in TagPalette.Icons)
        {
            Icons.Add(new PickOption(icon));
        }

        foreach (var color in TagPalette.Colors)
        {
            Colors.Add(new PickOption(color));
        }

        GameFilters.Add(GalleryFilters.AnyGame);
        TagFilters.Add(GalleryFilters.AnyTag);
        TagFilters.Add(GalleryFilters.NoTags);

        foreach (var option in GalleryFilters.Dates)
        {
            DateFilters.Add(option);
        }

        foreach (var option in GalleryFilters.Orders)
        {
            Orders.Add(option);
        }

        thumbs.Ready += (at, path, picture) => Post(() => Illustrate(at, path, picture));
        thumbs.Settled += at => Post(() => Rested(at));
        Loc.Track(this, static owner => ((GalleryModel)owner).Relocalize());
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? StorageChanged;

    public event Action? MarkRequested;

    public event Action? ApplyRequested;

    public event Action<ClipItem>? ClipArrived;

    public event Action? ThumbsSettled;

    public ObservableCollection<GameGroup> Groups { get; } = [];

    public ObservableCollection<TagChoice> TagChoices { get; } = [];

    public ObservableCollection<FilterOption> GameFilters { get; } = [];

    public ObservableCollection<FilterOption> TagFilters { get; } = [];

    public ObservableCollection<FilterOption> DateFilters { get; } = [];

    public ObservableCollection<FilterOption> Orders { get; } = [];

    public ObservableCollection<PickOption> Icons { get; } = [];

    public ObservableCollection<PickOption> Colors { get; } = [];

    public long OccupiedBytes { get; private set; }

    public int ClipCount { get; private set; }

    public long TaggedBytes
    {
        get
        {
            var total = 0L;

            foreach (var item in all)
            {
                if (item.Tags.Count > 0)
                {
                    total += item.SizeBytes;
                }
            }

            return total;
        }
    }

    public bool CanMark => recording;

    public CaptureCard? RecordingCard => recordingCard;

    public bool HasRecordingCard => recordingCard is not null;

    public bool CanApplyNow => recording && (settingsPending || lostBorderless);

    public string ApplyText => settingsPending ? Strings.Get("Gallery.ApplySettings") : Strings.Get("Gallery.ReturnBorderless");

    public string ApplyHint => settingsPending
        ? Strings.Get("Gallery.ApplyHint.Settings")
        : Strings.Get("Gallery.ApplyHint.Borderless");

    public bool CanTag => store is not null && chosen.Count > 0;

    public bool ManyChosen => chosen.Count > 1;

    public bool CanRemove => chosen.Count > 0;

    public bool CanReveal => selected is not null;

    public bool IsRemoving => removing;

    public bool IsCutting => cutting;

    public string RemoveQuestion => Doomed == 1
        ? Strings.Get("Gallery.RemoveOne")
        : Strings.Format("Gallery.RemoveMany", Doomed, Clips(Doomed));

    public string Trouble => trouble ?? string.Empty;

    public bool HasTrouble => trouble is not null;

    public string ChoiceText => Strings.Format("Tags.Choice", chosen.Count);

    public string TagDraft
    {
        get => draft;
        set
        {
            var next = value ?? string.Empty;

            if (string.Equals(draft, next, StringComparison.Ordinal))
            {
                return;
            }

            draft = next;
            Raise(nameof(TagDraft));
            Raise(nameof(CanCommit));
        }
    }

    public bool IsEditing => editing is not null;

    public string FormTitle => editing is null ? Strings.Get("Tags.FormNew") : Strings.Get("Tags.FormEdit");

    public string CommitText => editing is null ? Strings.Get("Tags.Create") : Strings.Get("Gallery.Save");

    public bool CanCommit => store is not null && draft.Trim().Length > 0;

    public string Search
    {
        get => search;
        set
        {
            var next = value ?? string.Empty;

            if (string.Equals(search, next, StringComparison.Ordinal))
            {
                return;
            }

            search = next;
            Raise(nameof(Search));
            Project();
        }
    }

    public FilterOption GameFilter
    {
        get => gameFilter;
        set => Switch(ref gameFilter, value, GalleryFilters.AnyGame, nameof(GameFilter));
    }

    public FilterOption TagFilter
    {
        get => tagFilter;
        set => Switch(ref tagFilter, value, GalleryFilters.AnyTag, nameof(TagFilter));
    }

    public FilterOption DateFilter
    {
        get => dateFilter;
        set => Switch(ref dateFilter, value, GalleryFilters.Dates[0], nameof(DateFilter));
    }

    public FilterOption Order
    {
        get => order;
        set => Switch(ref order, value, GalleryFilters.Orders[0], nameof(Order));
    }

    public bool IsGrid
    {
        get => !listing;
        set
        {
            if (value)
            {
                View(false);
            }
        }
    }

    public bool IsList
    {
        get => listing;
        set
        {
            if (value)
            {
                View(true);
            }
        }
    }

    public ClipItem? Selected
    {
        get => selected;
        set
        {
            if (value is null)
            {
                Pick([], null);
                return;
            }

            if (ReferenceEquals(selected, value) && chosen.Count == 1)
            {
                return;
            }

            Pick([value], value);
        }
    }

    public bool CanStepBack => Neighbour(-1) is not null;

    public bool CanStepNext => Neighbour(1) is not null;

    public string RecordingText
    {
        get
        {
            var tail = restart is null ? string.Empty : " · " + Strings.Format("Status.RecorderRestarted", Strings.Render(restart));

            if (recovery is not null)
            {
                tail += " · " + Strings.Format("Status.CaptureRecovered", Strings.Render(recovery));
            }

            if (sound is { } lost)
            {
                tail += " · " + (lost.Kind == "mic"
                    ? Strings.Get("Status.MicrophoneLost")
                    : Strings.Format("Status.SoundLost", Strings.Render(lost.Title)));
            }

            if (hotkey is { } broken)
            {
                tail += " · " + Strings.Format("Status.HotkeyBroken", broken.Combination, Strings.Render(broken.Complaint));
            }

            if (!recording)
            {
                return Strings.Get("Status.Idle") + tail;
            }

            var buffer = Sizes.Length(TimeSpan.FromSeconds(bufferSeconds));
            var memory = memoryBytes > 0 ? " · " + Strings.Format("Status.Memory", Sizes.Format(memoryBytes)) : string.Empty;
            var profile = profiled ? " · " + Strings.Get("Status.Profile") : string.Empty;
            var combination = sessionHotkey is null ? string.Empty : $" · {sessionHotkey}";
            var capture = captureNote is null ? string.Empty : $" · {Strings.Render(captureNote)}";
            var window = recordingCard is null ? string.Empty : " · " + Strings.Format("Status.Window", recordingCard.Window);

            return Strings.Format("Status.Recording", game ?? Strings.Get("Status.UnknownGame"), profile, window, buffer, combination, capture, memory, tail);
        }
    }

    public string StorageText
    {
        get
        {
            if (loading)
            {
                return Strings.Get("Gallery.Storage.Loading");
            }

            var total = Strings.Format("Gallery.Storage", Sizes.Format(OccupiedBytes), ClipCount);

            return Filtering ? Strings.Format("Gallery.Storage.Shown", total, shown) : total;
        }
    }

    private bool Filtering => search.Length > 0
        || !gameFilter.IsAll
        || !tagFilter.IsAll
        || !dateFilter.IsAll;

    private bool TagBound => !tagFilter.IsAll || search.Length > 0;

    private int Doomed => lonely ? 1 : chosen.Count;

    public void Load(string next)
    {
        var token = ++generation;
        root = next;

        thumbs.Cancel(token);
        store = null;
        loading = true;

        Groups.Clear();
        known.Clear();
        pending.Clear();
        all.Clear();
        TagChoices.Clear();
        Selected = null;
        Raise(nameof(CanTag));
        Raise(nameof(CanRemove));
        Raise(nameof(CanReveal));
        CancelRemove();
        NoteTrouble(null);
        CancelEdit();

        search = string.Empty;
        gameFilter = GalleryFilters.AnyGame;
        tagFilter = GalleryFilters.AnyTag;
        dateFilter = GalleryFilters.Dates[0];
        Raise(nameof(Search));
        Raise(nameof(DateFilter));
        Options();

        OccupiedBytes = 0;
        ClipCount = 0;
        shown = 0;

        Raise(nameof(StorageText));
        StorageChanged?.Invoke();

        Task.Run(() => Read(next)).ContinueWith(task => Post(() => Apply(token, task)));
    }

    public void Reread()
    {
        if (loading || refreshing || root is null)
        {
            return;
        }

        refreshing = true;
        var token = generation;
        var folder = root;

        Task.Run(() => ClipLibrary.Scan(folder)).ContinueWith(task => Post(() => Merge(token, task)));
    }

    public void Adopt(KebuzSettings next)
    {
        settings = next;
        Raise(nameof(RecordingText));

        if (!Enforce())
        {
            return;
        }

        Options();
        Project();
        StorageChanged?.Invoke();
    }

    public void Add(ClipEntry entry)
    {
        if (loading)
        {
            pending.Add(entry);
            return;
        }

        if (!Place(entry))
        {
            return;
        }

        thumbs.Want(generation, entry.Path, entry.Duration.TotalSeconds / 2);
        Enforce();

        Options();
        Project();
        StorageChanged?.Invoke();

        Announce(entry.Path);
    }

    public void SetRecording(RecordingState state)
    {
        recording = state.Recording;
        game = state.Game;
        profiled = state.Profiled;
        bufferSeconds = state.BufferSeconds;
        sessionHotkey = state.Hotkey;
        captureNote = state.Capture;
        settingsPending = state.SettingsPending;
        lostBorderless = state.LostBorderless();

        if (!state.Recording)
        {
            memoryBytes = 0;
            sound = null;
            recordingCard = null;
            Raise(nameof(RecordingCard));
            Raise(nameof(HasRecordingCard));
        }

        Raise(nameof(RecordingText));
        Raise(nameof(CanMark));
        Raise(nameof(CanApplyNow));
        Raise(nameof(ApplyText));
        Raise(nameof(ApplyHint));
    }

    public void SetCapture(CaptureCard card)
    {
        if (!recording)
        {
            return;
        }

        recordingCard = card;
        Raise(nameof(RecordingCard));
        Raise(nameof(HasRecordingCard));
        Raise(nameof(RecordingText));
    }

    public void SetMemory(long bytes)
    {
        memoryBytes = bytes;
        Raise(nameof(RecordingText));
    }

    public void RequestMark()
    {
        MarkRequested?.Invoke();
    }

    public void RequestApply()
    {
        ApplyRequested?.Invoke();
    }

    public void Choose(ClipItem clip, bool extend, bool range)
    {
        if (range && selected is not null && !ReferenceEquals(selected, clip))
        {
            var all = Flatten();
            var from = all.IndexOf(selected);
            var upto = all.IndexOf(clip);

            if (from >= 0 && upto >= 0)
            {
                var low = Math.Min(from, upto);
                var high = Math.Max(from, upto);
                Pick(all.GetRange(low, high - low + 1), selected);
                return;
            }
        }

        if (extend && chosen.Count > 0)
        {
            var next = Flatten()
                .Where(item => chosen.Contains(item) != ReferenceEquals(item, clip))
                .ToList();

            if (next.Count == 0)
            {
                return;
            }

            var anchor = next.Contains(clip)
                ? clip
                : selected is not null && next.Contains(selected) ? selected : next[0];

            Pick(next, anchor);
            return;
        }

        Pick([clip], clip);
    }

    public void Fold(GameGroup group)
    {
        group.IsCollapsed = !group.IsCollapsed;
        store?.Collapse(group.Title, group.IsCollapsed);
        store?.Save();

        if (!group.IsCollapsed)
        {
            return;
        }

        var hidden = group.Sessions.SelectMany(session => session.Clips).ToHashSet();

        if (!chosen.Overlaps(hidden))
        {
            return;
        }

        var kept = Flatten().Where(chosen.Contains).ToList();
        var anchor = selected is not null && kept.Contains(selected) ? selected : kept.FirstOrDefault();

        Pick(kept, anchor);
    }

    public void Step(int delta)
    {
        if (Neighbour(delta) is { } target)
        {
            Pick([target], target);
        }
    }

    public void Bring(ClipItem clip)
    {
        if (!known.ContainsKey(clip.Path))
        {
            return;
        }

        foreach (var group in Groups)
        {
            if (group.IsCollapsed && group.Sessions.Any(session => session.Clips.Contains(clip)))
            {
                group.IsCollapsed = false;
                store?.Collapse(group.Title, false);
                store?.Save();
            }
        }

        if (!Flatten().Contains(clip))
        {
            search = string.Empty;
            gameFilter = GalleryFilters.AnyGame;
            tagFilter = GalleryFilters.AnyTag;
            dateFilter = GalleryFilters.Dates[0];

            Raise(nameof(Search));
            Raise(nameof(GameFilter));
            Raise(nameof(TagFilter));
            Raise(nameof(DateFilter));
            Project();
        }

        Pick([clip], clip);
    }

    public void AskRemove(bool anchorOnly = false)
    {
        if (chosen.Count == 0 || removing)
        {
            return;
        }

        lonely = anchorOnly;
        removing = true;
        Raise(nameof(IsRemoving));
        Raise(nameof(RemoveQuestion));
    }

    public void CancelRemove()
    {
        if (!removing)
        {
            return;
        }

        removing = false;
        lonely = false;
        Raise(nameof(IsRemoving));
        Raise(nameof(RemoveQuestion));
    }

    public void Remove()
    {
        var alone = lonely;
        CancelRemove();

        var order = Flatten();
        var victims = alone
            ? order.Where(item => ReferenceEquals(item, selected)).ToList()
            : order.Where(chosen.Contains).ToList();

        if (victims.Count == 0)
        {
            return;
        }

        var heir = Successor(order, victims);
        var (stayed, result) = Banish(victims);

        if (stayed.Count > 0)
        {
            Pick(stayed, stayed[0]);
        }
        else if (heir is not null)
        {
            Pick([heir], heir);
        }
        else
        {
            Pick([], null);
        }

        Options();
        Project();
        StorageChanged?.Invoke();

        if (stayed.Count > 0)
        {
            NoteTrouble(Complaint(stayed.Count, victims.Count, result));
        }
    }

    public void Trim(ClipItem clip, double startSeconds, double endSeconds, bool replace, Action<string?> finished)
    {
        if (cutting || loading || !known.ContainsKey(clip.Path))
        {
            finished(Strings.Get("Gallery.Trim.Busy"));
            return;
        }

        cutting = true;
        Raise(nameof(IsCutting));

        var token = generation;
        var temporary = Path.Combine(
            Path.GetDirectoryName(clip.Path) ?? string.Empty,
            $"~kebuz-trim-{Guid.NewGuid():N}.mp4");

        Task.Run(() => ClipTrimmer.Cut(clip.Path, temporary, startSeconds, endSeconds))
            .ContinueWith(task => Post(() => Settle(token, clip, temporary, replace, task, finished)));
    }

    public void NoteTrouble(string? reason)
    {
        trouble = reason;
        Raise(nameof(Trouble));
        Raise(nameof(HasTrouble));
    }

    public bool Enforce()
    {
        if (loading || !settings.LimitFolder || !settings.DeleteOld)
        {
            return false;
        }

        var limit = Formats.LimitBytes(settings.LimitGigabytes);

        if (limit <= 0 || OccupiedBytes <= limit)
        {
            return false;
        }

        var target = Math.Max(
            0,
            limit - Formats.ClipBytes(settings.Profile, settings.BufferSeconds, settings.Audio));
        var loose = all.Where(item => item.Tags.Count == 0).ToList();
        loose.Sort((left, right) => Rank(left, right, "old"));

        var victims = new List<ClipItem>();
        var size = OccupiedBytes;

        foreach (var item in loose)
        {
            if (size <= target)
            {
                break;
            }

            victims.Add(item);
            size -= item.SizeBytes;
        }

        if (victims.Count == 0)
        {
            Log.Report(
                $"лимит папки перебран ({Sizes.Format(OccupiedBytes)} из {Sizes.Format(limit)}), " +
                "непомеченных клипов нет, удалять нечего");
            return false;
        }

        var (stayed, _) = Banish(victims);

        if (stayed.Count == victims.Count && victims.Count > 1)
        {
            Log.Report("пакет в корзину не прошёл целиком, добираю по одному клипу");
            stayed = Alone(victims);
        }

        Log.Report(
            $"автоудаление: клипов под лимит {victims.Count}, осталось на диске {stayed.Count}, " +
            $"занято {Sizes.Format(OccupiedBytes)} из {Sizes.Format(limit)}");

        return victims.Count != stayed.Count;
    }

    public void Toggle(TagChoice choice)
    {
        if (store is null || chosen.Count == 0)
        {
            return;
        }

        var add = choice.State != true;

        foreach (var item in chosen)
        {
            var ids = item.Tags.Select(tag => tag.Id).ToList();

            if (add)
            {
                if (!ids.Contains(choice.Tag.Id, StringComparer.Ordinal))
                {
                    ids.Add(choice.Tag.Id);
                }
            }
            else
            {
                ids.RemoveAll(id => string.Equals(id, choice.Tag.Id, StringComparison.Ordinal));
            }

            store.Assign(item.Path, ids);
            item.Retag(store.TagsOf(item.Path));
        }

        store.Save();
        Refresh();
        StorageChanged?.Invoke();

        if (TagBound)
        {
            Project();
        }
    }

    public void StartEdit(TagChoice choice)
    {
        editing = choice;
        draft = choice.Tag.Name;

        Mark(Icons, choice.Tag.Icon);
        Mark(Colors, choice.Tag.Color);
        RaiseForm();
    }

    public void CancelEdit()
    {
        editing = null;
        draft = string.Empty;

        Mark(Icons, string.Empty);
        Mark(Colors, string.Empty);
        RaiseForm();
    }

    public TagRecord? Commit()
    {
        var name = draft.Trim();

        if (store is null || name.Length == 0)
        {
            return null;
        }

        var icon = Picked(Icons);
        var color = Picked(Colors);
        TagRecord? tag;

        if (editing is null)
        {
            tag = store.Define(name, icon, color);
            TagChoices.Add(new TagChoice(tag));
        }
        else
        {
            tag = store.Redefine(editing.Tag.Id, name, icon, color);

            if (tag is null)
            {
                CancelEdit();
                return null;
            }

            editing.Tag = tag;

            foreach (var item in all)
            {
                if (Wears(item, tag))
                {
                    item.Retag(store.TagsOf(item.Path));
                }
            }
        }

        store.Save();
        CancelEdit();
        Refresh();
        Options();

        if (TagBound)
        {
            Project();
        }

        return tag;
    }

    public void AskDelete(TagChoice choice)
    {
        var clips = 0;
        var orphans = 0;

        foreach (var item in all)
        {
            if (!Wears(item, choice.Tag))
            {
                continue;
            }

            clips++;

            if (item.Tags.Count == 1)
            {
                orphans++;
            }
        }

        var many = Strings.Plural(clips, "Tags.ClipIn");

        choice.Warning = clips == 0
            ? Strings.Get("Tags.NoClips")
            : orphans == 0
                ? Strings.Format("Tags.Warning.Free", clips, many)
                : orphans == 1
                    ? Strings.Format("Tags.Warning.One", clips, many)
                    : Strings.Format("Tags.Warning.Many", clips, many, orphans);

        foreach (var other in TagChoices)
        {
            other.IsConfirming = ReferenceEquals(other, choice);
        }
    }

    public void Delete(TagChoice choice)
    {
        if (ReferenceEquals(editing, choice))
        {
            CancelEdit();
        }

        if (store is null)
        {
            TagChoices.Remove(choice);
            return;
        }

        var touched = all.Where(item => Wears(item, choice.Tag)).ToList();

        store.Undefine(choice.Tag.Id);
        store.Save();

        foreach (var item in touched)
        {
            item.Retag(store.TagsOf(item.Path));
        }

        TagChoices.Remove(choice);
        Refresh();
        Options();
        Project();
        StorageChanged?.Invoke();
    }

    public void NoteRestart(string reason)
    {
        restart = reason;
        Raise(nameof(RecordingText));
    }

    public void NoteSound(SourceHealth health)
    {
        sound = health.Live ? null : health;

        Raise(nameof(RecordingText));
    }

    public void NoteRecovery(string reason)
    {
        recovery = reason;
        Raise(nameof(RecordingText));
    }

    public void NoteHotkey(HotkeyState state)
    {
        hotkey = state.Registered ? null : state;

        Raise(nameof(RecordingText));
    }

    public void Dispose()
    {
        thumbs.Dispose();
    }

    private static (ClipStore Store, IReadOnlyList<ClipEntry> Clips) Read(string root)
    {
        return (ClipStore.Load(root), ClipLibrary.Scan(root));
    }

    private static string Clips(int count)
    {
        return Strings.Plural(count, "Gallery.Clip");
    }

    private static string Complaint(int stayed, int total, RecycleResult result)
    {
        var head = stayed == total
            ? Strings.Get("Gallery.Remove.Failed")
            : Strings.Format("Gallery.Remove.Partial", stayed, total);

        if (result.Code != 0)
        {
            return Strings.Format("Gallery.WithCode", head, result.Code);
        }

        return result.Aborted ? Strings.Format("Gallery.WithAborted", head) : head;
    }

    private static ClipItem? Successor(IReadOnlyList<ClipItem> order, IReadOnlyList<ClipItem> victims)
    {
        var doomed = new HashSet<ClipItem>(victims);
        var last = -1;

        for (var index = 0; index < order.Count; index++)
        {
            if (doomed.Contains(order[index]))
            {
                last = index;
            }
        }

        for (var index = last + 1; index < order.Count; index++)
        {
            if (!doomed.Contains(order[index]))
            {
                return order[index];
            }
        }

        for (var index = last - 1; index >= 0; index--)
        {
            if (!doomed.Contains(order[index]))
            {
                return order[index];
            }
        }

        return null;
    }

    private static bool Wears(ClipItem item, TagRecord tag)
    {
        foreach (var worn in item.Tags)
        {
            if (string.Equals(worn.Id, tag.Id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private ClipItem? Neighbour(int delta)
    {
        if (selected is null)
        {
            return null;
        }

        var visible = Flatten();
        var at = visible.IndexOf(selected);

        if (at < 0)
        {
            return null;
        }

        var next = at + delta;

        return next >= 0 && next < visible.Count ? visible[next] : null;
    }

    private List<ClipItem> Flatten()
    {
        var all = new List<ClipItem>();

        foreach (var group in Groups)
        {
            if (group.IsCollapsed)
            {
                continue;
            }

            foreach (var session in group.Sessions)
            {
                all.AddRange(session.Clips);
            }
        }

        return all;
    }

    private List<ClipItem> Alone(IReadOnlyList<ClipItem> victims)
    {
        var stayed = new List<ClipItem>();

        foreach (var victim in victims)
        {
            stayed.AddRange(Banish([victim]).Stayed);
        }

        return stayed;
    }

    private (List<ClipItem> Stayed, RecycleResult Result) Banish(IReadOnlyList<ClipItem> victims)
    {
        var result = ShellFiles.Recycle([.. victims.Select(item => item.Path)]);
        var stayed = new List<ClipItem>();
        var gone = 0;

        foreach (var victim in victims)
        {
            if (File.Exists(victim.Path))
            {
                stayed.Add(victim);
                continue;
            }

            Drop(victim);
            gone++;
        }

        if (gone > 0)
        {
            store?.Save();
            Log.Report($"в корзину ушло клипов: {gone}");
        }

        return (stayed, result);
    }

    private static string CutPath(string original)
    {
        var folder = Path.GetDirectoryName(original) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(original);
        var mark = name.IndexOf("_cut", StringComparison.OrdinalIgnoreCase);
        var stem = (mark > 0 ? name[..mark] : name) + "_cut";
        var path = Path.Combine(folder, $"{stem}.mp4");

        for (var index = 2; File.Exists(path); index++)
        {
            path = Path.Combine(folder, $"{stem}_{index}.mp4");
        }

        return path;
    }

    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"временный файл обрезки не удалился: {path}", ex);
        }
    }

    private void Settle(
        int token,
        ClipItem clip,
        string temporary,
        bool replace,
        Task<TrimResult> task,
        Action<string?> finished)
    {
        cutting = false;
        Raise(nameof(IsCutting));

        if (task.IsFaulted)
        {
            Log.Error($"обрезка {clip.Path} не удалась", task.Exception);
            finished(Strings.Get("Gallery.Trim.Failed"));
            return;
        }

        if (token != generation || !known.ContainsKey(clip.Path))
        {
            Discard(temporary);
            finished(Strings.Get("Gallery.Trim.Gone"));
            return;
        }

        var result = task.Result;

        Log.Report(
            $"обрезка {clip.FileName}: с {result.StartSeconds:0.000} с, длина {result.Duration.TotalSeconds:0.000} с, " +
            $"кадров {result.Samples}, звука {result.AudioFrames}, {Sizes.Format(result.SizeBytes)}, " +
            (replace ? "с заменой" : "копией"));

        var ids = store?.TagsOf(clip.Path).Select(tag => tag.Id).ToList() ?? [];
        var final = replace ? clip.Path : CutPath(clip.Path);

        if (replace)
        {
            ShellFiles.Recycle([clip.Path]);

            if (File.Exists(clip.Path))
            {
                Discard(temporary);
                finished(Strings.Get("Gallery.Trim.NotRecycled"));
                return;
            }

            Drop(clip);
        }

        try
        {
            File.Move(temporary, final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"обрезок не встал на место {final}", ex);

            if (!replace)
            {
                Discard(temporary);
                finished(Strings.Get("Gallery.Trim.NoSpot"));
                return;
            }

            var lost = Strings.Format("Gallery.Trim.Lost", Path.GetFileName(temporary));

            store?.Save();
            Pick([], null);
            Options();
            Project();
            StorageChanged?.Invoke();
            NoteTrouble(lost);
            finished(lost);
            return;
        }

        if (ids.Count > 0)
        {
            store?.Assign(final, ids);
        }

        store?.Save();

        var entry = ClipLibrary.Describe(final, clip.Game, clip.Session);
        Place(entry);
        thumbs.Want(generation, final, entry.Duration.TotalSeconds / 2);

        if (!replace)
        {
            Enforce();
        }

        Options();
        Project();
        StorageChanged?.Invoke();

        if (known.TryGetValue(final, out var fresh))
        {
            Pick([fresh], fresh);
        }

        finished(null);
    }

    private void Drop(ClipItem item)
    {
        known.Remove(item.Path);
        all.Remove(item);
        chosen.Remove(item);

        item.IsChosen = false;
        item.IsAnchor = false;

        OccupiedBytes -= item.SizeBytes;
        ClipCount--;

        store?.Forget(item.Path);
        ThumbnailCache.Forget(item.Path);
    }

    private void Pick(IReadOnlyList<ClipItem> next, ClipItem? anchor)
    {
        CancelRemove();
        NoteTrouble(null);

        var keep = new HashSet<ClipItem>(next);

        foreach (var item in chosen)
        {
            if (!keep.Contains(item))
            {
                item.IsChosen = false;
                item.IsAnchor = false;
            }
        }

        chosen.Clear();

        foreach (var item in next)
        {
            chosen.Add(item);
            item.IsChosen = true;
            item.IsAnchor = ReferenceEquals(item, anchor);
        }

        if (!ReferenceEquals(selected, anchor))
        {
            selected = anchor;
            Raise(nameof(Selected));
        }

        Raise(nameof(CanTag));
        Raise(nameof(ManyChosen));
        Raise(nameof(ChoiceText));
        Raise(nameof(CanRemove));
        Raise(nameof(CanReveal));
        Raise(nameof(RemoveQuestion));
        Raise(nameof(CanStepBack));
        Raise(nameof(CanStepNext));

        Refresh();
    }

    private void Refresh()
    {
        foreach (var choice in TagChoices)
        {
            var worn = 0;

            foreach (var item in chosen)
            {
                if (Wears(item, choice.Tag))
                {
                    worn++;
                }
            }

            choice.State = chosen.Count > 0 && worn == chosen.Count
                ? true
                : worn == 0 ? false : null;
        }
    }

    private static void Mark(ObservableCollection<PickOption> options, string value)
    {
        foreach (var option in options)
        {
            option.IsPicked = string.Equals(option.Value, value, StringComparison.Ordinal);
        }
    }

    private static string Picked(ObservableCollection<PickOption> options)
    {
        foreach (var option in options)
        {
            if (option.IsPicked)
            {
                return option.Value;
            }
        }

        return string.Empty;
    }

    private static FilterOption Carry(
        ObservableCollection<FilterOption> target,
        FilterOption current,
        FilterOption fallback)
    {
        if (target.Contains(current))
        {
            return current;
        }

        foreach (var option in target)
        {
            if (string.Equals(option.Key, current.Key, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return fallback;
    }

    private static int Rank(ClipItem left, ClipItem right, string key)
    {
        var by = key switch
        {
            "old" => left.When.CompareTo(right.When),
            "long" => right.Length.CompareTo(left.Length),
            "big" => right.SizeBytes.CompareTo(left.SizeBytes),
            _ => right.When.CompareTo(left.When)
        };

        if (by != 0)
        {
            return by;
        }

        by = right.When.CompareTo(left.When);

        return by != 0 ? by : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(ObservableCollection<FilterOption> target, int head, IReadOnlyList<FilterOption> next)
    {
        if (target.Count - head != next.Count)
        {
            return false;
        }

        for (var index = 0; index < next.Count; index++)
        {
            var current = target[head + index];
            var wanted = next[index];

            if (!string.Equals(current.Key, wanted.Key, StringComparison.Ordinal)
                || !string.Equals(current.Label, wanted.Label, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void Sync(ObservableCollection<FilterOption> target, int head, IReadOnlyList<FilterOption> next)
    {
        if (Same(target, head, next))
        {
            return;
        }

        var kept = new Dictionary<string, FilterOption>(StringComparer.Ordinal);

        for (var index = head; index < target.Count; index++)
        {
            kept[target[index].Key] = target[index];
        }

        while (target.Count > head)
        {
            target.RemoveAt(target.Count - 1);
        }

        foreach (var option in next)
        {
            var reused = kept.TryGetValue(option.Key, out var old)
                && string.Equals(old.Label, option.Label, StringComparison.Ordinal);

            target.Add(reused ? kept[option.Key] : option);
        }
    }

    private void Switch(ref FilterOption field, FilterOption? next, FilterOption fallback, string name)
    {
        if (next is null && syncing)
        {
            return;
        }

        var pick = next ?? fallback;

        if (ReferenceEquals(field, pick))
        {
            return;
        }

        field = pick;
        Raise(name);
        Project();
    }

    private void View(bool next)
    {
        if (listing == next)
        {
            return;
        }

        listing = next;
        Raise(nameof(IsGrid));
        Raise(nameof(IsList));
    }

    private bool Fits(ClipItem item)
    {
        if (!gameFilter.IsAll && !string.Equals(item.Game, gameFilter.Key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!tagFilter.IsAll && !Tagged(item))
        {
            return false;
        }

        if (!dateFilter.IsAll && item.When < GalleryFilters.Since(dateFilter.Key))
        {
            return false;
        }

        return search.Length == 0 || item.Matches(search);
    }

    private bool Tagged(ClipItem item)
    {
        if (string.Equals(tagFilter.Key, GalleryFilters.Untagged, StringComparison.Ordinal))
        {
            return item.Tags.Count == 0;
        }

        foreach (var tag in item.Tags)
        {
            if (string.Equals(tag.Id, tagFilter.Key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void Project()
    {
        var visible = new List<ClipItem>();

        foreach (var item in all)
        {
            if (Fits(item))
            {
                visible.Add(item);
            }
        }

        var key = order.Key;
        visible.Sort((left, right) => Rank(left, right, key));

        Groups.Clear();

        foreach (var item in visible)
        {
            var group = Land(item.Game);
            Land(group, item.Session, item.When).Clips.Add(item);
            group.Note(item.When);
        }

        shown = visible.Count;
        Raise(nameof(StorageText));

        Reconcile(visible);

        Raise(nameof(CanStepBack));
        Raise(nameof(CanStepNext));
    }

    private void Reconcile(IReadOnlyList<ClipItem> visible)
    {
        if (visible.Count == 0)
        {
            if (chosen.Count > 0 || selected is not null)
            {
                Pick([], null);
            }

            return;
        }

        var live = new List<ClipItem>();

        foreach (var item in visible)
        {
            if (chosen.Contains(item))
            {
                live.Add(item);
            }
        }

        if (live.Count == 0)
        {
            Pick([visible[0]], visible[0]);
            return;
        }

        var anchor = selected is not null && live.Contains(selected) ? selected : live[0];

        if (live.Count == chosen.Count && ReferenceEquals(anchor, selected))
        {
            return;
        }

        Pick(live, anchor);
    }

    private void Options()
    {
        syncing = true;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<FilterOption>();

        foreach (var item in all)
        {
            if (seen.Add(item.Game))
            {
                games.Add(new FilterOption(item.Game, item.Game));
            }
        }

        games.Sort((left, right) => string.Compare(left.Label, right.Label, StringComparison.OrdinalIgnoreCase));

        var tags = new List<FilterOption>();

        foreach (var tag in store?.Tags ?? [])
        {
            tags.Add(new FilterOption(tag.Id, tag.Label));
        }

        Sync(GameFilters, 1, games);
        Sync(TagFilters, 2, tags);

        syncing = false;

        gameFilter = Carry(GameFilters, gameFilter, GalleryFilters.AnyGame);
        tagFilter = Carry(TagFilters, tagFilter, GalleryFilters.AnyTag);

        Raise(nameof(GameFilter));
        Raise(nameof(TagFilter));
    }

    private void RaiseForm()
    {
        Raise(nameof(TagDraft));
        Raise(nameof(IsEditing));
        Raise(nameof(FormTitle));
        Raise(nameof(CommitText));
        Raise(nameof(CanCommit));
    }

    private void Post(Action action)
    {
        if (context is null)
        {
            action();
            return;
        }

        context.Post(_ => action(), null);
    }

    private void Apply(int token, Task<(ClipStore Store, IReadOnlyList<ClipEntry> Clips)> task)
    {
        if (token != generation)
        {
            return;
        }

        loading = false;
        var late = new List<string>();

        if (task.IsFaulted)
        {
            Log.Error("не удалось прочитать библиотеку клипов", task.Exception);
        }
        else
        {
            store = task.Result.Store;

            foreach (var tag in store.Tags)
            {
                TagChoices.Add(new TagChoice(tag));
            }

            foreach (var entry in task.Result.Clips)
            {
                Place(entry);
            }

            foreach (var entry in pending)
            {
                if (Place(entry))
                {
                    late.Add(entry.Path);
                }
            }

            Enforce();

            if (store.Prune(known.Keys))
            {
                store.Save();
            }

            thumbs.Sweep(token, known.Keys);

            foreach (var item in all)
            {
                thumbs.Want(token, item.Path, item.Middle);
            }
        }

        pending.Clear();

        Options();
        Project();
        RaiseForm();

        StorageChanged?.Invoke();

        foreach (var path in late)
        {
            Announce(path);
        }
    }

    private void Merge(int token, Task<IReadOnlyList<ClipEntry>> task)
    {
        refreshing = false;

        if (token != generation || loading)
        {
            return;
        }

        if (task.IsFaulted)
        {
            Log.Error("не удалось перечитать папку клипов", task.Exception);
            return;
        }

        var added = 0;

        foreach (var entry in task.Result)
        {
            if (Place(entry))
            {
                thumbs.Want(token, entry.Path, entry.Duration.TotalSeconds / 2);
                added++;
            }
        }

        if (added == 0)
        {
            return;
        }

        Log.Report($"папка клипов перечитана, новых клипов: {added}");

        Enforce();
        Options();
        Project();
        StorageChanged?.Invoke();
    }

    private void Announce(string path)
    {
        if (known.TryGetValue(path, out var item))
        {
            ClipArrived?.Invoke(item);
        }
    }

    private bool Place(ClipEntry entry)
    {
        if (known.ContainsKey(entry.Path))
        {
            return false;
        }

        var item = new ClipItem(entry, Output(), store?.TagsOf(entry.Path) ?? []);

        known.Add(entry.Path, item);
        all.Add(item);

        OccupiedBytes += entry.SizeBytes;
        ClipCount++;

        return true;
    }

    private void Illustrate(int at, string path, ImageSource picture)
    {
        if (at != generation)
        {
            return;
        }

        if (known.TryGetValue(path, out var item))
        {
            item.Shot = picture;
        }
    }

    private void Rested(int at)
    {
        if (at == generation)
        {
            ThumbsSettled?.Invoke();
        }
    }

    private GameGroup Land(string title)
    {
        foreach (var group in Groups)
        {
            if (string.Equals(group.Title, title, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        var fresh = new GameGroup(title, DateTime.MinValue)
        {
            IsCollapsed = store?.IsCollapsed(title) ?? false
        };

        Groups.Add(fresh);
        return fresh;
    }

    private SessionGroup Land(GameGroup group, string folder, DateTime fallback)
    {
        foreach (var session in group.Sessions)
        {
            if (string.Equals(session.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                return session;
            }
        }

        var fresh = new SessionGroup(folder, ClipLibrary.SessionStamp(folder, fallback));
        group.Sessions.Add(fresh);
        return fresh;
    }

    private string Output()
    {
        return Formats.Output(settings.Profile);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
