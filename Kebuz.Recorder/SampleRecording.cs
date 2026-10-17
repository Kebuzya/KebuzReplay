using Kebuz.Recorder.Buffering;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Interop;
using Kebuz.Recorder.Settings;
using Kebuz.Recorder.Watching;
using Windows.Graphics.Capture;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Kebuz.Recorder;

public sealed record SampleClip(
    string Path,
    long Bytes,
    TimeSpan Length,
    uint Width,
    uint Height,
    uint FrameRate,
    string? Game);

public static class SampleRecording
{
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Segment = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Room = TimeSpan.FromSeconds(30);

    public static SampleClip Record(KebuzSettings settings, nint near, string path, Action<string> trace)
    {
        SampleClip? done = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                done = Run(settings, near, path, trace);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            Name = "KebuzSample",
            IsBackground = true
        };

        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }

        return done!;
    }

    private static unsafe SampleClip Run(KebuzSettings settings, nint near, string path, Action<string> trace)
    {
        var (window, game) = ListedGame();
        var item = window != 0
            ? CaptureItemInterop.CreateForWindow(window)
            : CaptureItemInterop.CreateForMonitor((nint)PInvoke.MonitorFromWindow(new HWND((void*)near), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value);

        var quality = settings.Profile;
        var video = VideoEncoderSettings.Create((uint)item.Size.Width, (uint)item.Size.Height, quality);
        var peak = VideoEncoderSettings.PeakOf(quality, null);

        using var device = GraphicsDevice.Create();
        using var buffer = new ReplayBuffer(
            device,
            video,
            new ReplayBufferSettings(Segment, Room, ReplayBuffer.EstimatePeakBytes(peak, Room, Room) * 2, Room),
            trace);
        using var runner = new CaptureRunner(
            device,
            item,
            buffer,
            new CaptureRequest(video.SourceWidth, video.SourceHeight, video.FrameRate, trace));

        trace($"пример: {(game ?? "экран")}, {video.SourceWidth}x{video.SourceHeight} -> {video.Width}x{video.Height} @ {video.FrameRate}");

        runner.Start();
        Thread.Sleep(Length);

        using var hold = buffer.BeginClip();
        runner.Stop();

        var clip = buffer.Save(path, hold) ?? throw new InvalidOperationException("пример пуст: захват не дал ни одного кадра");

        return new SampleClip(clip.Path, clip.FileSizeBytes, clip.Duration, video.Width, video.Height, video.FrameRate, game);
    }

    private static (nint Window, string? Game) ListedGame()
    {
        var apps = WatchedApps.Load();
        var running = new List<RunningProcess>();
        ProcessScanner.Snapshot(running);

        foreach (var process in running)
        {
            var app = apps.Match(process.Executable, () => ProcessScanner.TryGetPath(process.Id));

            if (app is null)
            {
                continue;
            }

            var window = GameWindow.FindLargest(process.Id);

            if (window != 0)
            {
                return (window, app.Title);
            }
        }

        return (0, null);
    }
}
