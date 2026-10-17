using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Kebuz.Gui.Gallery;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Player;

public sealed class PlayerModel : INotifyPropertyChanged, IDisposable
{
    private const int SettleFrames = 90;
    private const double VolumeStep = 0.02;
    private const double MinTrim = 0.5;
    private const double Ticks = 10_000_000;

    private readonly GalleryModel gallery;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;

    private ClipPlayer? player;
    private ClipItem? clip;
    private Func<string>? failure;
    private bool attached;
    private bool playing;
    private bool suspended;
    private bool quiet;
    private bool pictured;
    private bool fullScreen;
    private bool scrubbing;
    private double played;
    private double total;
    private double volume = 1;
    private int settle;
    private bool trimming;
    private bool asking;
    private double trimStart;
    private double trimEnd;
    private double span;
    private IReadOnlyList<double>? frames;
    private IReadOnlyList<double>? keys;
    private Func<string?>? trimNote;
    private int trimToken;

    public PlayerModel(GalleryModel gallery)
    {
        this.gallery = gallery;
        gallery.PropertyChanged += OnGalleryChanged;
        Loc.Track(this, static owner => ((PlayerModel)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Redraw;

    public GalleryModel Gallery => gallery;

    public ImageSource? Frame => player?.Image;

    public bool HasClip => clip is not null;

    public bool HasPicture => pictured;

    public bool Scrubbing
    {
        get => scrubbing;
        set
        {
            if (scrubbing == value)
            {
                return;
            }

            scrubbing = value;

            if (player is null)
            {
                return;
            }

            if (value)
            {
                player.Pause();
            }
            else if (playing)
            {
                player.Play();
            }
        }
    }

    public double Volume
    {
        get => volume;
        set
        {
            var level = Math.Clamp(value, 0, 1);

            if (Math.Abs(volume - level) < 0.001)
            {
                return;
            }

            volume = level;
            player?.SetVolume(level);
            Raise();
        }
    }

    public bool IsPlaying => playing;

    public bool CanStepBack => gallery.CanStepBack && !trimming;

    public bool CanStepNext => gallery.CanStepNext && !trimming;

    public string FailureText => failure?.Invoke() ?? string.Empty;

    public bool IsBroken => failure is not null;

    public string PlayName => playing ? Strings.Get("Player.Pause") : Strings.Get("Player.Play");

    public double TotalSeconds => total;

    public double PlayedSeconds
    {
        get => played;
        set
        {
            if (Math.Abs(played - value) < 0.001)
            {
                return;
            }

            played = value;
            Raise();
            Raise(nameof(PositionText));

            if (quiet || player is null)
            {
                return;
            }

            player.Seek(value);
            settle = SettleFrames;
            Redraw?.Invoke();
        }
    }

    public string PositionText
    {
        get
        {
            var at = Sizes.Length(TimeSpan.FromSeconds(played));
            var upto = total > 0
                ? Sizes.Length(TimeSpan.FromSeconds(total))
                : clip is null ? "0:00" : clip.Duration;

            return $"{at} / {upto}";
        }
    }

    public string HeadlineText => clip?.HeadlineText ?? string.Empty;

    public string DetailsText => clip?.DetailsText ?? string.Empty;

    public bool IsTrimming => trimming;

    public bool CanTrim => clip is not null && failure is null && !gallery.ManyChosen && !trimming;

    public bool IsSaveAsked => asking;

    public bool CanSaveTrim => trimming
        && frames is not null
        && !gallery.IsCutting
        && trimEnd - trimStart >= MinTrim
        && (trimStart > 0 || trimEnd < span);

    public bool CanMarkTrim => trimming && frames is not null && !gallery.IsCutting;

    public double TrimStartSeconds => trimStart;

    public double TrimEndSeconds => trimEnd;

    public GridLength TrimHead => new(Math.Max(0, trimStart), GridUnitType.Star);

    public GridLength TrimBody => new(Math.Max(0, trimEnd - trimStart), GridUnitType.Star);

    public GridLength TrimTail => new(Math.Max(0, total - trimEnd), GridUnitType.Star);

    public string TrimText => Strings.Format(
        "Trim.Range",
        Stamp(trimStart),
        Stamp(trimEnd),
        trimEnd - trimStart);

    public string TrimNote => gallery.IsCutting
        ? Strings.Get("Trim.Note.Cutting")
        : trimNote?.Invoke() ?? (trimming && frames is null ? Strings.Get("Trim.Note.Finding") : string.Empty);

    public bool IsFullScreen
    {
        get => fullScreen;
        set
        {
            if (fullScreen == value)
            {
                return;
            }

            fullScreen = value;
            Raise();
        }
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        attached = true;

        try
        {
            var fresh = ClipPlayer.Create();
            fresh.Loaded += OnLoaded;
            fresh.Ended += OnEnded;
            fresh.Failed += OnFailed;
            fresh.Revived += OnRevived;
            fresh.SetVolume(volume);
            player = fresh;
        }
        catch (Exception ex)
        {
            failure = () => Strings.Get("Player.EngineFailed");
            Log.Error("плеер предпросмотра не создан", ex);
            Raise(nameof(IsBroken));
            Raise(nameof(FailureText));
            return;
        }

        Raise(nameof(Frame));
        Show(gallery.Selected);
    }

    public void Nudge(int notches)
    {
        Volume += notches * VolumeStep;
    }

    public void Toggle()
    {
        if (playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    public void Play()
    {
        if (player is null || clip is null)
        {
            return;
        }

        if (played >= total && total > 0)
        {
            player.Seek(trimming ? trimStart : 0);
        }

        player.Play();
        playing = true;
        settle = 0;
        Raise(nameof(IsPlaying));
        Raise(nameof(PlayName));
        Redraw?.Invoke();
    }

    public void Pause()
    {
        player?.Pause();
        playing = false;
        Raise(nameof(IsPlaying));
        Raise(nameof(PlayName));
    }

    public void Step(int delta)
    {
        if (!trimming)
        {
            gallery.Step(delta);
        }
    }

    public void BeginTrim()
    {
        if (!CanTrim || clip is null)
        {
            return;
        }

        trimming = true;
        asking = false;
        trimNote = null;
        frames = null;
        keys = null;
        trimStart = 0;
        trimEnd = total;

        var token = ++trimToken;
        var path = clip.Path;

        Task.Run(() => ClipTrimmer.Layout(path)).ContinueWith(task => Post(() => Learn(token, task)));

        RaiseTrim();
    }

    public void EndTrim()
    {
        if (!trimming)
        {
            return;
        }

        trimming = false;
        asking = false;
        trimNote = null;
        frames = null;
        keys = null;
        trimToken++;

        RaiseTrim();
    }

    public void MarkStart()
    {
        if (!CanMarkTrim)
        {
            return;
        }

        trimStart = Snap(played);

        if (trimEnd - trimStart < MinTrim)
        {
            trimEnd = span;
        }

        Settled();
    }

    public void MarkEnd()
    {
        if (!CanMarkTrim || frames is null)
        {
            return;
        }

        var at = FrameAt(played);
        var end = at + 1 < frames.Count ? frames[at + 1] : span;

        if (end - trimStart < MinTrim)
        {
            var start = Snap(end - MinTrim);

            if (end - start < MinTrim)
            {
                trimNote = () => Strings.Get("Trim.Note.Short");
                RaiseTrim();
                return;
            }

            trimStart = start;
        }

        trimEnd = end;
        Settled();
    }

    public void StepFrame(int delta)
    {
        if (!CanMarkTrim || frames is null)
        {
            return;
        }

        Pause();

        var at = Math.Clamp(FrameAt(played) + delta, 0, frames.Count - 1);
        PlayedSeconds = frames[at];
    }

    public void AskSave()
    {
        if (!CanSaveTrim)
        {
            return;
        }

        asking = true;
        RaiseTrim();
    }

    public void CancelSave()
    {
        if (!asking)
        {
            return;
        }

        asking = false;
        RaiseTrim();
    }

    public void SaveTrim(bool replace)
    {
        if (!CanSaveTrim || clip is null)
        {
            return;
        }

        Pause();

        asking = false;
        trimNote = null;

        var token = trimToken;

        gallery.Trim(clip, trimStart, trimEnd, replace, trouble => Post(() => Saved(token, trouble)));

        RaiseTrim();
    }

    public void Suspend()
    {
        suspended = true;
        Pause();
    }

    public void Resume()
    {
        if (!suspended)
        {
            return;
        }

        suspended = false;
        settle = SettleFrames;
        Redraw?.Invoke();
    }

    public bool Draw()
    {
        if (player is null || suspended)
        {
            return false;
        }

        if (player.Draw() && !pictured)
        {
            pictured = true;
            Raise(nameof(HasPicture));
        }

        if (playing)
        {
            Sync();
            return true;
        }

        if (settle > 0)
        {
            settle--;
        }

        return settle > 0;
    }

    public void Dispose()
    {
        gallery.PropertyChanged -= OnGalleryChanged;
        player?.Dispose();
        player = null;
    }

    private void OnGalleryChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GalleryModel.Selected):
                Show(gallery.Selected);
                break;

            case nameof(GalleryModel.CanStepBack):
                Raise(nameof(CanStepBack));
                break;

            case nameof(GalleryModel.CanStepNext):
                Raise(nameof(CanStepNext));
                break;

            case nameof(GalleryModel.ManyChosen):
                Raise(nameof(CanTrim));
                break;

            case nameof(GalleryModel.IsCutting):
                RaiseTrim();
                break;
        }
    }

    private void Show(ClipItem? next)
    {
        EndTrim();

        clip = next;
        playing = false;
        pictured = false;
        total = next?.Seconds ?? 0;

        if (player is not null)
        {
            failure = null;
        }

        quiet = true;
        PlayedSeconds = 0;
        quiet = false;

        Raise(nameof(HasClip));
        Raise(nameof(HasPicture));
        Raise(nameof(IsBroken));
        Raise(nameof(FailureText));
        Raise(nameof(IsPlaying));
        Raise(nameof(PlayName));
        Raise(nameof(TotalSeconds));
        Raise(nameof(PositionText));
        Raise(nameof(HeadlineText));
        Raise(nameof(DetailsText));
        Raise(nameof(CanTrim));

        if (player is null)
        {
            return;
        }

        player.Pause();

        if (next is null)
        {
            return;
        }

        player.Open(next.Path);
        settle = SettleFrames;
        Redraw?.Invoke();
    }

    private void OnLoaded()
    {
        if (player is null)
        {
            return;
        }

        var length = player.Duration;

        if (length > 0)
        {
            total = length;
            Raise(nameof(TotalSeconds));
            Raise(nameof(PositionText));
        }

        settle = SettleFrames;
        Redraw?.Invoke();
    }

    private void OnRevived()
    {
        settle = SettleFrames;
        Redraw?.Invoke();
    }

    private void OnEnded()
    {
        playing = false;
        Sync();
        Raise(nameof(IsPlaying));
        Raise(nameof(PlayName));
    }

    private void OnFailed(string reason)
    {
        playing = false;
        pictured = false;
        failure = () => Strings.Format("Player.Failed", reason);
        Log.Error($"плеер предпросмотра: клип не открылся (код {reason})");

        Raise(nameof(IsPlaying));
        Raise(nameof(PlayName));
        Raise(nameof(IsBroken));
        Raise(nameof(FailureText));
        Raise(nameof(HasPicture));
    }

    private void Sync()
    {
        if (player is null || player.IsSeeking || Scrubbing)
        {
            return;
        }

        var before = played;

        quiet = true;
        PlayedSeconds = Math.Min(player.Position, total);
        quiet = false;

        if (trimming && playing && before < trimEnd && played >= trimEnd)
        {
            PlayedSeconds = trimStart;
        }
    }

    private static string Stamp(double seconds)
    {
        var whole = Math.Max(0, seconds);
        var minutes = (int)(whole / 60);

        return string.Create(Strings.Culture, $"{minutes}:{whole - (minutes * 60):00.0}");
    }

    private void Learn(int token, Task<ClipLayout> task)
    {
        if (token != trimToken || !trimming)
        {
            return;
        }

        if (task.IsFaulted)
        {
            Log.Error("обрезка: кадры клипа не прочитались", task.Exception);
            trimNote = () => Strings.Get("Trim.Note.NoRead");
            RaiseTrim();
            return;
        }

        var layout = task.Result;

        if (layout.Video.Count == 0 || layout.KeyFrames.Count == 0)
        {
            trimNote = () => Strings.Get("Trim.Note.NoKeys");
            RaiseTrim();
            return;
        }

        frames = [.. layout.Video.Select(sample => sample.Ticks / Ticks)];
        keys = layout.KeyFrames;

        var last = layout.Video[^1];
        span = (last.Ticks + last.DurationTicks) / Ticks;
        trimEnd = span;

        RaiseTrim();
    }

    private void Saved(int token, string? trouble)
    {
        if (token != trimToken)
        {
            return;
        }

        if (trouble is null)
        {
            EndTrim();
            return;
        }

        trimNote = () => trouble;
        RaiseTrim();
    }

    private double Snap(double seconds)
    {
        if (keys is null || keys.Count == 0)
        {
            return 0;
        }

        var found = keys[0];

        foreach (var key in keys)
        {
            if (key > seconds + 0.0005)
            {
                break;
            }

            found = key;
        }

        return found;
    }

    private int FrameAt(double seconds)
    {
        if (frames is null || frames.Count == 0)
        {
            return 0;
        }

        var at = 0;

        for (var index = 0; index < frames.Count; index++)
        {
            if (frames[index] > seconds + 0.0005)
            {
                break;
            }

            at = index;
        }

        return at;
    }

    private void Settled()
    {
        trimNote = null;
        asking = false;
        RaiseTrim();
    }

    private void RaiseTrim()
    {
        Raise(nameof(IsTrimming));
        Raise(nameof(CanTrim));
        Raise(nameof(CanStepBack));
        Raise(nameof(CanStepNext));
        Raise(nameof(IsSaveAsked));
        Raise(nameof(CanSaveTrim));
        Raise(nameof(CanMarkTrim));
        Raise(nameof(TrimStartSeconds));
        Raise(nameof(TrimEndSeconds));
        Raise(nameof(TrimHead));
        Raise(nameof(TrimBody));
        Raise(nameof(TrimTail));
        Raise(nameof(TrimText));
        Raise(nameof(TrimNote));
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

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
