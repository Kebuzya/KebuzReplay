using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Kebuz.Gui.Localization;
using Kebuz.Gui.Player;
using Kebuz.Recorder;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Settings;

public sealed class SampleModel : INotifyPropertyChanged, IDisposable
{
    private const int SettleFrames = 90;

    private readonly Func<KebuzSettings, nint, string, SampleClip> record;
    private readonly Func<ClipPlayer?> createPlayer;

    private ClipPlayer? player;
    private SampleClip? clip;
    private string? failure;
    private bool recording;
    private bool busy;
    private bool open;
    private bool pictured;
    private int settle;
    private int generation;

    public SampleModel()
        : this((settings, near, path) => SampleRecording.Record(settings, near, path, Log.Trace), ClipPlayer.Create)
    {
    }

    public SampleModel(Func<KebuzSettings, nint, string, SampleClip> record, Func<ClipPlayer?> createPlayer)
    {
        this.record = record;
        this.createPlayer = createPlayer;
        Loc.Track(this, static owner => ((SampleModel)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Redraw;

    public static string SamplePath => Path.Combine(AppPaths.Local, "kebuz-sample.mp4");

    public bool CanStart => !recording && !busy;

    public bool IsBlocked => recording;

    public bool IsBusy => busy;

    public bool IsOpen
    {
        get => open;
        set
        {
            if (open == value)
            {
                return;
            }

            open = value;
            Raise();

            if (!value)
            {
                Close();
            }
        }
    }

    public ImageSource? Frame => player?.Image;

    public bool HasPicture => pictured;

    public string StateText => busy
        ? Strings.Format("Settings.Sample.Recording", SampleRecording.Length.TotalSeconds)
        : failure is not null
            ? Strings.Format("Settings.Sample.Failed", failure)
            : string.Empty;

    public string InfoText => clip is null
        ? string.Empty
        : Strings.Format(
            "Settings.Sample.Info",
            clip.Width,
            clip.Height,
            clip.FrameRate,
            Sizes.Format(clip.Bytes),
            Sizes.Format(PerMinute(clip)),
            clip.Game ?? Strings.Get("Settings.Sample.Screen"));

    public static long PerMinute(SampleClip sample)
    {
        return sample.Length.TotalSeconds <= 0 ? 0 : (long)(sample.Bytes / sample.Length.TotalSeconds * 60);
    }

    public void SetRecording(bool next)
    {
        if (recording == next)
        {
            return;
        }

        recording = next;
        Raise(nameof(CanStart));
        Raise(nameof(IsBlocked));
    }

    public async void Start(KebuzSettings settings, nint near)
    {
        if (!CanStart)
        {
            return;
        }

        var own = ++generation;

        Release();
        busy = true;
        failure = null;
        clip = null;
        IsOpen = true;
        RaiseAll();

        SampleClip? made = null;
        string? trouble = null;

        try
        {
            made = await Task.Run(() => record(settings, near, SamplePath));
        }
        catch (Exception ex)
        {
            Log.Error("пример не записан", ex);
            trouble = ex.Message;
        }

        if (own != generation)
        {
            Drop(made?.Path);
            return;
        }

        busy = false;
        clip = made;
        failure = trouble;

        if (made is not null && open)
        {
            Play(made.Path);
        }
        else if (!open)
        {
            Drop(made?.Path);
        }

        RaiseAll();
    }

    public bool Draw()
    {
        if (player is null)
        {
            return false;
        }

        if (player.Draw() && !pictured)
        {
            pictured = true;
            Raise(nameof(HasPicture));
        }

        if (!player.IsPaused)
        {
            return true;
        }

        return settle-- > 0;
    }

    public void Dispose()
    {
        generation++;
        Release();
    }

    private void Play(string path)
    {
        try
        {
            player ??= Attach();

            if (player is null)
            {
                return;
            }

            player.Open(path);
            settle = SettleFrames;
            Raise(nameof(Frame));
            Redraw?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("пример не открылся в плеере", ex);
            failure = ex.Message;
        }
    }

    private ClipPlayer? Attach()
    {
        var fresh = createPlayer();

        if (fresh is null)
        {
            return null;
        }

        fresh.SetVolume(0);
        fresh.Loaded += () =>
        {
            fresh.Play();
            settle = SettleFrames;
            Redraw?.Invoke();
        };
        fresh.Ended += () =>
        {
            fresh.Seek(0);
            fresh.Play();
            Redraw?.Invoke();
        };
        fresh.Failed += reason =>
        {
            failure = reason;
            Raise(nameof(StateText));
        };
        return fresh;
    }

    private void Close()
    {
        generation++;
        busy = false;
        Release();
        RaiseAll();
    }

    private void Release()
    {
        if (player is not null)
        {
            player.Dispose();
            player = null;
        }

        pictured = false;
        Drop(SamplePath);
    }

    private static void Drop(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Report($"файл примера не удалён: {ex.Message}");
        }
    }

    private void Relocalize()
    {
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
