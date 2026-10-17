using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Buffering;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Ipc;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Kebuz.Recorder.Watching;

internal sealed record ReplaySessionSettings(
    QualityProfile Quality,
    uint? FrameRate,
    uint? BitrateBps,
    TimeSpan SegmentLength,
    TimeSpan BufferLength,
    long MaxBytes,
    TimeSpan MergeDelay,
    TimeSpan MergeMaxLength,
    AudioTrackSettings? Audio = null,
    Settings.CaptureMode Capture = Settings.CaptureMode.Standard)
{
    public uint PeakBitrateBps => VideoEncoderSettings.PeakOf(Quality, BitrateBps);

    public ReplaySessionSettings Apply(Settings.KebuzSettings applied)
    {
        var quality = applied.Profile;
        var bufferLength = TimeSpan.FromSeconds(applied.BufferSeconds);
        var mergeMaxLength = TimeSpan.FromSeconds(applied.MergeMaxSeconds);
        var peak = VideoEncoderSettings.PeakOf(quality, BitrateBps);

        return this with
        {
            Quality = quality,
            BufferLength = bufferLength,
            MergeDelay = TimeSpan.FromSeconds(applied.MergeSeconds),
            MergeMaxLength = mergeMaxLength,
            MaxBytes = ReplayBuffer.EstimatePeakBytes(peak, bufferLength, mergeMaxLength) * 2,
            Audio = Audio is null ? null : AudioTrackSettings.Of(applied.Audio),
            Capture = applied.Capture
        };
    }
}

internal sealed class ReplaySession : IDisposable
{
    public const string BorderlessTrouble = "Trouble.BorderlessBlank";

    private static readonly TimeSpan BlankLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DarkNotice = TimeSpan.FromSeconds(3);

    private readonly GraphicsDevice device;
    private readonly ReplayBuffer buffer;
    private readonly ClipScheduler scheduler;
    private readonly CaptureRunner runner;
    private readonly Action<string> trace;

    private ThumbnailHost? host;
    private nint game;
    private uint processId;
    private bool stopped;
    private bool disposed;
    private TimeSpan darkest;
    private long? frontDarkSince;

    private ReplaySession(
        string folder,
        GraphicsDevice device,
        ReplayBuffer buffer,
        ClipScheduler scheduler,
        CaptureRunner runner,
        VideoEncoderSettings video,
        ThumbnailHost? host,
        string capture,
        Action<string> trace,
        nint game,
        uint processId)
    {
        Folder = folder;
        this.game = game;
        this.processId = processId;
        Video = video;
        Capture = capture;

        this.host = host;
        this.device = device;
        this.buffer = buffer;
        this.scheduler = scheduler;
        this.runner = runner;
        this.trace = trace;
    }

    public string Folder { get; }

    public string Capture { get; private set; }

    public bool Borderless => host is not null;

    public nint Window => game;

    public string Encoder => buffer.Name;

    public VideoEncoderSettings Video { get; }

    public string Geometry => buffer.Scaling
        ? $"{Video.SourceWidth}x{Video.SourceHeight} в {Video.Width}x{Video.Height}"
        : $"{Video.Width}x{Video.Height} без масштабирования";

    public string Controls => $"{buffer.Controls}, вход {buffer.InputFormat}";

    public AudioTrack? Audio => buffer.Audio;

    public string Sound => buffer.Audio is { } track
        ? string.Join(", ", track.Sources.Select(source => source.Title))
        : "нет";

    public int Clips => scheduler.Clips;

    public SegmentRingStatus Status => buffer.Status;

    public long MemoryBytes => buffer.Status.Bytes + (buffer.Audio?.Status.Capacity ?? 0);

    public static ReplaySession Start(
        nint hwnd,
        uint processId,
        string folder,
        ReplaySessionSettings settings,
        Action<ScheduledClip> saved,
        Action<string> trace,
        Action<SourceHealth>? health = null,
        TimeSpan simulateLossAfter = default,
        Action<ShotImage>? shot = null)
    {
        var device = GraphicsDevice.Create(simulateLossAfter);
        ReplayBuffer? buffer = null;
        ClipScheduler? scheduler = null;
        CaptureRunner? runner = null;
        ThumbnailHost? host = null;

        try
        {
            var (item, opened, capture) = Source(hwnd, settings.Capture, trace);
            host = opened;

            var video = VideoEncoderSettings.Create(
                (uint)item.Size.Width,
                (uint)item.Size.Height,
                settings.Quality,
                settings.FrameRate,
                settings.BitrateBps);

            buffer = new ReplayBuffer(
                device,
                video,
                new ReplayBufferSettings(
                    settings.SegmentLength,
                    settings.BufferLength,
                    settings.MaxBytes,
                    settings.MergeMaxLength,
                    settings.Audio,
                    processId,
                    health),
                trace);

            scheduler = new ClipScheduler(
                buffer,
                new ClipMergeSettings(settings.MergeDelay, settings.MergeMaxLength),
                (_, at) => SessionPaths.ClipPath(folder, at),
                saved,
                trace);

            runner = new CaptureRunner(
                device,
                item,
                buffer,
                new CaptureRequest(video.SourceWidth, video.SourceHeight, video.FrameRate, trace, host is not null, shot));

            runner.Start();

            return new ReplaySession(folder, device, buffer, scheduler, runner, video, host, capture, trace, hwnd, processId);
        }
        catch
        {
            runner?.Dispose();
            scheduler?.Dispose();
            buffer?.Dispose();
            host?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public void Blackout()
    {
        runner.Blackout();

        host?.Dispose();
        host = null;
        game = 0;
        processId = 0;
        frontDarkSince = null;
        darkest = TimeSpan.Zero;
        Capture = "чёрный кадр, окна игры нет";

        buffer.Audio?.Retarget(null);
    }

    public void Reattach(nint hwnd, uint nextProcessId, Settings.CaptureMode mode, Action<ShotImage>? shot)
    {
        var (item, opened, capture) = Source(hwnd, mode, trace);

        host?.Dispose();
        host = opened;
        game = hwnd;
        processId = nextProcessId;
        frontDarkSince = null;
        darkest = TimeSpan.Zero;
        Capture = capture;

        trace($"окно игры вернулось: {capture}, размер {item.Size.Width}x{item.Size.Height}, пишется в {Video.SourceWidth}x{Video.SourceHeight}");
        runner.Attach(item, opened is not null, shot);
        buffer.Audio?.Retarget(nextProcessId);
    }

    private static (Windows.Graphics.Capture.GraphicsCaptureItem Item, ThumbnailHost? Host, string Capture) Source(
        nint hwnd,
        Settings.CaptureMode mode,
        Action<string> trace)
    {
        if (mode is not Settings.CaptureMode.Borderless)
        {
            return (CaptureTarget.Window(hwnd), null, "стандартный WGC");
        }

        ThumbnailHost? host = null;

        try
        {
            host = ThumbnailHost.Create(hwnd);
            var item = CaptureTarget.Window(host.Handle);
            return (item, host, $"без рамки, превью DWM {host.Width}x{host.Height}");
        }
        catch (Exception ex)
        {
            host?.Dispose();
            trace($"захват без рамки не поднялся, беру стандартный: {ex.Message}");
            return (CaptureTarget.Window(hwnd), null, $"стандартный WGC: без рамки не поднялся ({ex.Message})");
        }
    }

    public void Trigger()
    {
        scheduler.Trigger();
    }

    public string? Trouble()
    {
        if (device.LossReason() is { } reason)
        {
            return Localization.Strings.Code("Trouble.DeviceLost", reason.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (runner.Failure is { } failure)
        {
            return Localization.Strings.Code("Trouble.EncodingStopped", failure.Message);
        }

        if (host is null)
        {
            return null;
        }

        var dark = runner.BlankFor;
        Notice(dark);

        if (dark > TimeSpan.Zero && InFront())
        {
            frontDarkSince ??= System.Diagnostics.Stopwatch.GetTimestamp();
        }
        else
        {
            frontDarkSince = null;
        }

        if (frontDarkSince is { } since && System.Diagnostics.Stopwatch.GetElapsedTime(since) >= BlankLimit)
        {
            return Localization.Strings.Code(BorderlessTrouble, BlankLimit.TotalSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        }

        return null;
    }

    public void Abort()
    {
        if (stopped)
        {
            return;
        }

        stopped = true;

        try
        {
            runner.Stop();
        }
        catch (Exception ex)
        {
            trace($"аварийное закрытие захвата: {ex.Message}");
        }
        finally
        {
            scheduler.Dispose();
        }
    }

    public CaptureResult Stop()
    {
        if (stopped)
        {
            throw new InvalidOperationException("Сессия уже остановлена");
        }

        stopped = true;

        try
        {
            return runner.Stop();
        }
        finally
        {
            scheduler.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        scheduler.Dispose();
        runner.Dispose();
        buffer.Dispose();
        host?.Dispose();
        device.Dispose();
    }

    private void Notice(TimeSpan dark)
    {
        if (dark >= DarkNotice && darkest < DarkNotice)
        {
            trace($"превью тёмное {dark.TotalSeconds:F0} с: {Scene()}");
        }

        if (dark > TimeSpan.Zero)
        {
            darkest = dark > darkest ? dark : darkest;
            return;
        }

        if (darkest >= DarkNotice)
        {
            trace($"превью снова живое, темнело {darkest.TotalSeconds:F0} с: {Scene()}");
        }

        darkest = TimeSpan.Zero;
    }

    private unsafe string Scene()
    {
        var window = new HWND(game);
        var front = PInvoke.GetForegroundWindow();
        uint frontId = 0;

        if (front != HWND.Null)
        {
            PInvoke.GetWindowThreadProcessId(front, &frontId);
        }

        var whose = InFront() ? "окно игры" : frontId == processId ? "другое окно игры" : $"pid {frontId} {Name(frontId)}";
        var cloaked = 0;
        var cloak = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Succeeded
            ? cloaked.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "?";
        var place = PInvoke.GetWindowRect(window, out var rect)
            ? $"{rect.right - rect.left}x{rect.bottom - rect.top} в ({rect.left},{rect.top})"
            : "окна нет";

        return $"на переднем плане {whose}, окно игры свёрнуто {(bool)PInvoke.IsIconic(window)}, скрыто {cloak}, {place}";
    }

    private bool InFront()
    {
        var window = new HWND(game);
        var front = PInvoke.GetForegroundWindow();

        return front == window
            || (front != HWND.Null && PInvoke.GetAncestor(front, Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOTOWNER) == window);
    }

    private static string Name(uint id)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)id);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "(процесса уже нет)";
        }
    }
}
