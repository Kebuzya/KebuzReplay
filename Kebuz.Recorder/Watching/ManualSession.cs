using System.Text.Json;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Buffering;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Interop;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Settings;
using Windows.Graphics.Capture;

namespace Kebuz.Recorder.Watching;

internal sealed class ManualSession : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly CaptureRunner runner;
    private readonly ReplayBuffer? buffer;
    private readonly ClipScheduler? scheduler;
    private readonly ContinuousEncoder? continuous;
    private bool stopped;
    private bool disposed;
    private readonly GraphicsCaptureItem? item;
    private readonly DesktopCapture? desktop;
    private readonly ThumbnailHost? host;
    private bool sourceClosed;

    public CaptureSource Source { get; }
    public string Group { get; }
    public string Folder { get; }
    public bool Continuous => continuous is not null;
    public double Seconds => continuous?.Seconds ?? buffer?.Status.Duration.TotalSeconds ?? 0;
    public long Bytes => continuous?.Bytes ?? (buffer?.Status.Bytes ?? 0) + (buffer?.Audio?.Status.Capacity ?? 0);
    public Exception? Failure => (Volatile.Read(ref sourceClosed) ? new RecordingFailure("Recording.SourceClosed") : null)
        ?? continuous?.Failure ?? runner.Failure
        ?? (device.LossReason() is { } reason ? new IOException($"Устройство D3D потеряно: 0x{reason:X8}") : null);

    public ManualSession(CaptureSource source, string group, KebuzSettings common, ReplaySessionSettings template,
        Action<ScheduledClip> saved, Action<string> trace, Action<SourceHealth> health, Action<CaptureShot> shown)
    {
        Source = source;
        Group = group;
        var manual = common.Manual ?? ManualRecordingSettings.From(common);
        var effective = manual.Apply(common);
        if (source.Kind == CaptureSourceKind.Monitor)
            effective = effective with { Audio = effective.Audio with { GameSound = false } };
        var settings = template.Apply(effective);
        var folder = SessionPaths.SessionFolder(common.EffectiveClipsRoot, group, DateTime.Now);
        Folder = folder;
        for (var suffix = 2; Directory.Exists(Folder); suffix++) Folder = folder + "_" + suffix;
        if (source.Kind == CaptureSourceKind.Monitor)
        {
            desktop = DesktopCapture.Open((nint)source.Handle, out device);
        }
        else
        {
            device = GraphicsDevice.Create();
            var opened = ReplaySession.OpenSource((nint)source.Handle, CaptureModeFor(source.Kind), trace);
            item = opened.Item;
            host = opened.Host;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            if (item is not null) item.Closed += OnSourceClosed;
            var size = desktop?.Size ?? item!.Size;
            var video = VideoEncoderSettings.Create((uint)size.Width, (uint)size.Height,
                settings.Quality, settings.FrameRate, settings.BitrateBps);
            File.WriteAllText(Path.Combine(Folder, "session.json"), JsonSerializer.Serialize(
                new SessionMetadata(source, DateTime.Now, manual.Method, video.Width, video.Height, video.FrameRate)));
            IVideoEncoder encoder;
            if (manual.Method == RecordingMethod.Continuous)
            {
                RecordingRecovery.Begin(Folder, group);
                continuous = new ContinuousEncoder(device, video, settings.Audio,
                    source.ProcessId == 0 ? null : source.ProcessId, Path.Combine(Folder, RecordingRecovery.SourceName), trace, health);
                encoder = continuous;
            }
            else
            {
                buffer = new ReplayBuffer(device, video, new ReplayBufferSettings(settings.SegmentLength,
                    settings.BufferLength, settings.MaxBytes, settings.MergeMaxLength, settings.Audio,
                    source.ProcessId == 0 ? null : source.ProcessId, health), trace);
                scheduler = new ClipScheduler(buffer, new ClipMergeSettings(settings.MergeDelay, settings.MergeMaxLength),
                    (_, at) => SessionPaths.ClipPath(Folder, at), saved, trace);
                encoder = buffer;
            }
            runner = desktop is not null
                ? new CaptureRunner(device, desktop, encoder, new CaptureRequest(video.SourceWidth,
                    video.SourceHeight, video.FrameRate, trace, false, shot => shown(new CaptureShot(group,
                        source.Title, shot.SourceWidth, shot.SourceHeight, shot.Png, DateTime.UtcNow)),
                    continuous is null ? null : TimeSpan.FromSeconds(20)))
                : new CaptureRunner(device, item!, encoder, new CaptureRequest(video.SourceWidth,
                video.SourceHeight, video.FrameRate, trace, host is not null, shot => shown(new CaptureShot(group,
                    source.Title, shot.SourceWidth, shot.SourceHeight, shot.Png, DateTime.UtcNow)),
                continuous is null ? null : TimeSpan.FromSeconds(20)));
            runner.Start();
        }
        catch
        {
            if (item is not null) item.Closed -= OnSourceClosed;
            desktop?.Dispose();
            host?.Dispose();
            scheduler?.Dispose();
            continuous?.Dispose();
            buffer?.Dispose();
            device.Dispose();
            CleanEmptyFolder();
            throw;
        }
    }

    public void Mark() => scheduler?.Trigger();
    internal static CaptureMode CaptureModeFor(CaptureSourceKind kind) =>
        kind is CaptureSourceKind.Window ? CaptureMode.Borderless : CaptureMode.Standard;
    private void OnSourceClosed(GraphicsCaptureItem sender, object args) => Volatile.Write(ref sourceClosed, true);

    public ClipInfo? Stop(bool saveBuffer)
    {
        if (stopped) return null;
        stopped = true;
        if (saveBuffer) Mark();
        try { runner.Stop(); }
        finally { scheduler?.Dispose(); }
        if (continuous?.Failure is { } error) throw new IOException("Запись сохранена для восстановления", error);
        if (continuous is not null && continuous.Seconds <= 0)
        {
            CleanEmptyFolder();
            return null;
        }
        return continuous is null ? null : RecordingRecovery.Complete(Folder) with
        {
            WithoutMicrophone = continuous.MicrophoneMissing
        };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (item is not null) item.Closed -= OnSourceClosed;
        try { if (!stopped) Stop(false); }
        finally
        {
            runner.Dispose();
            scheduler?.Dispose();
            continuous?.Dispose();
            buffer?.Dispose();
            host?.Dispose();
            device.Dispose();
            CleanEmptyFolder();
        }
    }

    private void CleanEmptyFolder()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var files = Directory.GetFileSystemEntries(Folder);
            var metadata = Path.Combine(Folder, "session.json");
            var pending = Path.Combine(Folder, RecordingRecovery.ManifestName);
            if (files.All(path => path == metadata || path == pending))
            {
                if (File.Exists(pending)) File.Delete(pending);
                files = Directory.GetFileSystemEntries(Folder);
            }
            if (files.Length == 0)
            {
                Directory.Delete(Folder);
                return;
            }
            if (files.Length != 1 || files[0] != metadata) return;
            File.Delete(metadata);
            Directory.Delete(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Report($"не удалось убрать пустую ручную сессию {Folder}: {ex.Message}");
        }
    }
}
