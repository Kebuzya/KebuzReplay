using System.Globalization;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Buffering;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Input;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Watching;

namespace Kebuz.Recorder;

public static class RecorderHost
{
    private static readonly TimeSpan SettingsTimeout = TimeSpan.FromSeconds(10);

    public static int Run(string[] args)
    {
        var exitCode = 1;

        var thread = new Thread(() =>
        {
            try
            {
                exitCode = Execute(args);
            }
            catch (Exception ex)
            {
                Log.Error("прогон прерван", ex);
                exitCode = 1;
            }
        });

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        Log.Flush();

        return exitCode;
    }

    public static string? Reject(string[] args)
    {
        try
        {
            CommandLine.Parse(args);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return ex.Message;
        }
    }

    private static int Execute(string[] args)
    {
        if (Reject(args) is { } rejected)
        {
            Log.Error(rejected);
            return 2;
        }

        var options = CommandLine.Parse(args);
        var quality = QualityPresets.Get(options.Quality);

        Log.Configure(!options.Supervised, options.Trace);

        if (options.SampleOut is { } sample)
        {
            var clip = SampleRecording.Record(
                Settings.KebuzSettings.Default with { Quality = options.Quality },
                Windows.Win32.PInvoke.GetDesktopWindow(),
                sample,
                Log.Trace);
            Console.WriteLine($"Пример: {clip.Path}, {clip.Width}x{clip.Height} @ {clip.FrameRate}, {clip.Length.TotalSeconds:F2} с, {clip.Bytes} байт, источник {clip.Game ?? "экран"}");
            return 0;
        }

        if (options.Probe)
        {
            CaptureProbe.Run(options.Duration);
            return 0;
        }

        if (options.HotkeyProbe)
        {
            HotkeyProbe.Run(Console.WriteLine);
            return 0;
        }

        if (options.AudioProbe)
        {
            AudioProbe.Run(options.Duration, options.AudioMic, options.AudioDevice, Log.Trace);
            return 0;
        }

        if (options.AudioMix)
        {
            AudioMixProbe.Run(new AudioMixProbeRequest(
                options.Duration,
                options.OutputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                    ? options.OutputPath
                    : $"mix_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav",
                Sound(options),
                Log.Trace));

            return 0;
        }

        if (options.AudioStream)
        {
            AudioStreamProbe.Run(new AudioStreamProbeRequest(
                options.Duration,
                options.OutputPath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
                    ? options.OutputPath
                    : null,
                options.AudioKeepSeconds,
                options.AudioRingSeconds,
                Sound(options),
                Log.Trace));

            return 0;
        }

        if (options.AudioDenoiseFormats)
        {
            AudioDenoiseProbe.Formats();
            return 0;
        }

        if (options.AudioDenoise)
        {
            AudioDenoiseProbe.Run(options.Duration, options.AudioDevice, options.AudioInput, options.OutputPath, Log.Trace);
            return 0;
        }

        if (options.BorderlessProbe is not null)
        {
            Capture.BorderlessProbe.Run(options.BorderlessProbe);
            return 0;
        }

        if (options.DetectProbe)
        {
            DetectProbe.Run(options.Duration);
            return 0;
        }

        if (options.Encoders)
        {
            EncoderProbe.Run(quality, options.FrameRate, options.BitrateBps);
            return 0;
        }

        if (options.Stream)
        {
            SampleStreamProbe.Run(
                options.Duration,
                quality,
                options.FrameRate,
                options.BitrateBps,
                Log.Trace);
            return 0;
        }

        Action<string> trace = Log.Trace;

        if (options.Apps || options.AddApp is not null || options.RemoveApp is not null)
        {
            return ManageApps(options);
        }

        if (options.Watch)
        {
            using var link = options.Supervised
                ? SupervisorLink.Connect(TimeSpan.FromSeconds(10))
                : null;

            var applied = link?.WaitForSettings(SettingsTimeout);

            if (options.Supervised && applied is null)
            {
                Log.Report(
                    $"Настройки от супервизора не пришли за {SettingsTimeout.TotalSeconds:F0} с, " +
                    "иду на дефолтах CLI");
            }
            else if (applied is not null)
            {
                Log.Report($"Настройки получены от супервизора: {applied}");
            }

            var video = BufferSettings(options, quality);

            WatchHost.Run(new WatchRequest(
                WatchedApps.Load(),
                new WatchSettings(
                    applied?.EffectiveClipsRoot ?? options.ClipsRoot ?? SessionPaths.DefaultRoot,
                    TimeSpan.FromSeconds(options.GraceSeconds),
                    TimeSpan.FromSeconds(options.ScanSeconds),
                    TimeSpan.FromSeconds(options.WindowSeconds),
                    TimeSpan.FromSeconds(options.FailDeviceSeconds),
                    options.FailDeviceTimes,
                    options.FailRestarts,
                    options.KeepAfterExitSeconds is { } keep ? TimeSpan.FromSeconds(keep) : null,
                    TimeSpan.FromSeconds(options.MarkSeconds),
                    options.FollowLarger ? true : null),
                applied is null ? video : video.Apply(applied),
                applied,
                Hotkey.Parse(applied?.Hotkey ?? options.Hotkey),
                TimeSpan.FromSeconds(options.WatchSeconds),
                trace,
                options.Supervised,
                link is null ? null : link.SignalReady,
                link is null ? null : link.WhenShutdown,
                link is null ? null : link.WhenSettings,
                link is null ? null : link.WhenMarkClip,
                link is null ? null : link.Send,
                link is null ? null : link.WhenLevelWatch,
                options.AudioMeter,
                link is null ? null : link.WhenApplyNow,
                options.ShotOut));

            return 0;
        }

        if (options.Buffer)
        {
            var settings = BufferSettings(options, quality);

            ReplayBufferProbe.Run(new ReplayBufferProbeRequest(
                options.OutputPath,
                options.Duration,
                settings.SegmentLength,
                settings.BufferLength,
                settings.MaxBytes,
                settings.Quality,
                settings.FrameRate,
                settings.BitrateBps,
                Hotkey.Parse(options.Hotkey),
                settings.MergeDelay,
                settings.MergeMaxLength,
                settings.Audio,
                TimeSpan.FromSeconds(options.MarkSeconds),
                trace));

            return 0;
        }

        var request = new RecordingRequest(
            options.OutputPath,
            options.Duration,
            quality,
            options.FrameRate,
            options.BitrateBps,
            options.WindowTitle,
            trace);

        Console.WriteLine($"Запись {options.Duration.TotalSeconds:F0} с в {Path.GetFullPath(options.OutputPath)}");
        var result = ClipRecording.Run(request);

        var effectiveBitrate = result.FileSizeBytes * 8d / result.Elapsed.TotalSeconds / 1_000_000d;

        Log.Flush();

        Console.WriteLine($"Пресет: {quality.Title}");
        Console.WriteLine(result.Video.Scaled
            ? $"Разрешение: {result.Video.SourceWidth}x{result.Video.SourceHeight} в {result.Video.Width}x{result.Video.Height} @ {result.Video.FrameRate}"
            : $"Разрешение: {result.Video.Width}x{result.Video.Height} @ {result.Video.FrameRate} без масштабирования");
        Console.WriteLine($"Управление: {result.Controls}");
        Console.WriteLine($"Кадров захвачено: {result.FramesCaptured}, закодировано: {result.FramesWritten}, отброшено: {result.FramesDropped}");
        Console.WriteLine($"Размер файла: {Sizes.Format(result.FileSizeBytes)} ({effectiveBitrate:F2} Мбит/с)");
        return 0;
    }

    private static AudioTrackSettings Sound(CommandLineOptions options)
    {
        var levels = options.AudioLevels;

        return new AudioTrackSettings(
            !options.AudioNoDesktop,
            options.AudioDevice,
            Loudness(levels, "desktop"),
            false,
            Loudness(levels, "game"),
            options.AudioProcess is null
                ? []
                : [new AudioAppTarget(options.AudioProcess, Loudness(levels, "process"))],
            options.AudioMic,
            null,
            Loudness(levels, "mic"),
            options.AudioNoiseSuppression);
    }

    private static int Loudness(IReadOnlyDictionary<string, int> levels, string key)
    {
        return levels.TryGetValue(key, out var level) ? level : 100;
    }

    private static ReplaySessionSettings BufferSettings(CommandLineOptions options, QualityProfile quality)
    {
        var bufferLength = TimeSpan.FromSeconds(options.BufferSeconds);
        var mergeMaxLength = TimeSpan.FromSeconds(options.MergeMaxSeconds);
        var peak = VideoEncoderSettings.PeakOf(quality, options.BitrateBps);

        var maxBytes = options.BufferMegabytes > 0
            ? (long)options.BufferMegabytes * 1024 * 1024
            : ReplayBuffer.EstimatePeakBytes(peak, bufferLength, mergeMaxLength) * 2;

        return new ReplaySessionSettings(
            quality,
            options.FrameRate,
            options.BitrateBps,
            TimeSpan.FromSeconds(options.SegmentSeconds),
            bufferLength,
            maxBytes,
            TimeSpan.FromSeconds(options.MergeSeconds),
            mergeMaxLength,
            options.NoAudio ? null : Sound(options),
            options.Capture);
    }

    private static int ManageApps(CommandLineOptions options)
    {
        var apps = WatchedApps.Load();

        if (options.AddApp is not null)
        {
            var added = apps.Add(options.AddApp);
            apps.Save();
            Console.WriteLine($"Добавлено: {added.Executable}{(added.Path is null ? string.Empty : $" ({added.Path})")}");
        }

        if (options.RemoveApp is not null)
        {
            var removed = apps.Remove(options.RemoveApp);
            apps.Save();
            Console.WriteLine(removed ? $"Убрано: {options.RemoveApp}" : $"В списке нет {options.RemoveApp}");
        }

        Console.WriteLine($"Список: {apps.StoragePath}");

        if (apps.Items.Count == 0)
        {
            Console.WriteLine("Пусто");
            return 0;
        }

        foreach (var app in apps.Items)
        {
            Console.WriteLine($"  {app.Executable}  {app.Title}{(app.Path is null ? string.Empty : $"  {app.Path}")}");
        }

        return 0;
    }
}

internal sealed record CommandLineOptions(
    bool Probe,
    bool HotkeyProbe,
    bool AudioProbe,
    bool AudioMix,
    bool AudioStream,
    bool AudioMic,
    bool AudioNoDesktop,
    bool NoAudio,
    bool AudioMeter,
    bool AudioDenoiseFormats,
    bool AudioDenoise,
    bool DetectProbe,
    string? BorderlessProbe,
    string? AudioInput,
    bool AudioNoiseSuppression,
    Settings.CaptureMode Capture,
    double AudioKeepSeconds,
    double AudioRingSeconds,
    string? AudioDevice,
    string? AudioProcess,
    IReadOnlyDictionary<string, int> AudioLevels,
    bool Encoders,
    bool Stream,
    bool Buffer,
    bool Watch,
    bool Apps,
    bool Supervised,
    bool Trace,
    string OutputPath,
    TimeSpan Duration,
    QualityPreset Quality,
    uint? FrameRate,
    uint? BitrateBps,
    double SegmentSeconds,
    double BufferSeconds,
    uint BufferMegabytes,
    string Hotkey,
    double MergeSeconds,
    double MergeMaxSeconds,
    double MarkSeconds,
    double WatchSeconds,
    double GraceSeconds,
    double ScanSeconds,
    double WindowSeconds,
    double FailDeviceSeconds,
    int FailDeviceTimes,
    int FailRestarts,
    string? ClipsRoot,
    string? AddApp,
    string? RemoveApp,
    string? WindowTitle,
    string? ShotOut = null,
    double? KeepAfterExitSeconds = null,
    bool FollowLarger = false,
    string? SampleOut = null);

internal static class CommandLine
{
    public static CommandLineOptions Parse(string[] args)
    {
        var probe = false;
        var hotkeyProbe = false;
        var audioProbe = false;
        var audioMix = false;
        var audioStream = false;
        var audioMic = false;
        var audioNoDesktop = false;
        var noAudio = false;
        var audioMeter = false;
        var audioDenoiseFormats = false;
        var audioDenoise = false;
        var detectProbe = false;
        string? borderlessProbe = null;
        string? audioInput = null;
        var audioNoiseSuppression = false;
        var capture = Settings.CaptureMode.Standard;
        var audioKeepSeconds = 0d;
        var audioRingSeconds = 0d;
        string? audioDevice = null;
        string? audioProcess = null;
        var audioLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var encoders = false;
        var stream = false;
        var buffer = false;
        var watch = false;
        var apps = false;
        var supervised = false;
        var trace = false;
        var seconds = 10d;
        var quality = QualityPresets.Default;
        uint? fps = null;
        uint? bitrate = null;
        var segmentSeconds = 3d;
        var bufferSeconds = 30d;
        var bufferMegabytes = 0u;
        var hotkey = "Ctrl+Alt+R";
        var mergeSeconds = 8d;
        var mergeMaxSeconds = 20d;
        var markSeconds = 0d;
        var watchSeconds = 0d;
        var graceSeconds = 45d;
        var scanSeconds = 1d;
        var windowSeconds = 60d;
        var failDeviceSeconds = 0d;
        var failDeviceTimes = 1;
        var failRestarts = 0;
        string? clipsRoot = null;
        string? addApp = null;
        string? removeApp = null;
        string? output = null;
        string? window = null;
        string? shotOut = null;
        double? keepAfterExit = null;
        var followLarger = false;
        string? sampleOut = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--probe":
                    probe = true;
                    break;
                case "--hotkey-probe":
                    hotkeyProbe = true;
                    break;
                case "--audio-probe":
                    audioProbe = true;
                    break;
                case "--audio-mix":
                    audioMix = true;
                    break;
                case "--audio-stream":
                    audioStream = true;
                    break;
                case "--audio-keep-seconds":
                    audioKeepSeconds = Number(Next(args, ref i));
                    break;
                case "--audio-ring-seconds":
                    audioRingSeconds = Number(Next(args, ref i));
                    break;
                case "--audio-mic":
                    audioMic = true;
                    break;
                case "--audio-no-desktop":
                    audioNoDesktop = true;
                    break;
                case "--capture":
                    capture = Enum.Parse<Settings.CaptureMode>(Next(args, ref i), true);
                    break;

                case "--audio-ns":
                    audioNoiseSuppression = true;
                    break;

                case "--audio-in":
                    audioInput = Next(args, ref i);
                    break;

                case "--audio-denoise":
                    audioDenoise = true;
                    break;

                case "--borderless-probe":
                    borderlessProbe = Next(args, ref i);
                    break;

                case "--detect-probe":
                    detectProbe = true;
                    break;

                case "--audio-denoise-formats":
                    audioDenoiseFormats = true;
                    break;

                case "--audio-meter":
                    audioMeter = true;
                    break;

                case "--no-audio":
                    noAudio = true;
                    break;
                case "--audio-device":
                    audioDevice = Next(args, ref i);
                    break;
                case "--audio-process":
                    audioProcess = Next(args, ref i);
                    break;
                case "--audio-level":
                    Level(audioLevels, Next(args, ref i));
                    break;
                case "--encoders":
                    encoders = true;
                    break;
                case "--stream":
                    stream = true;
                    break;
                case "--buffer":
                    buffer = true;
                    break;
                case "--watch":
                    watch = true;
                    break;
                case "--apps":
                    apps = true;
                    break;
                case "--supervised":
                    supervised = true;
                    break;
                case "--add-app":
                    addApp = Next(args, ref i);
                    break;
                case "--remove-app":
                    removeApp = Next(args, ref i);
                    break;
                case "--clips":
                    clipsRoot = Next(args, ref i);
                    break;
                case "--watch-seconds":
                    watchSeconds = Number(Next(args, ref i));
                    break;
                case "--grace-seconds":
                    graceSeconds = Number(Next(args, ref i));
                    break;
                case "--scan-seconds":
                    scanSeconds = Number(Next(args, ref i));
                    break;
                case "--window-seconds":
                    windowSeconds = Number(Next(args, ref i));
                    break;
                case "--fail-device-after":
                    failDeviceSeconds = Number(Next(args, ref i));
                    break;
                case "--fail-device-times":
                    failDeviceTimes = int.Parse(Next(args, ref i));
                    break;
                case "--fail-restarts":
                    failRestarts = int.Parse(Next(args, ref i));
                    break;
                case "--segment-seconds":
                    segmentSeconds = Number(Next(args, ref i));
                    break;
                case "--buffer-seconds":
                    bufferSeconds = Number(Next(args, ref i));
                    break;
                case "--buffer-mb":
                    bufferMegabytes = uint.Parse(Next(args, ref i));
                    break;
                case "--hotkey":
                    hotkey = Next(args, ref i);
                    break;
                case "--merge-seconds":
                    mergeSeconds = Number(Next(args, ref i));
                    break;
                case "--merge-max-seconds":
                    mergeMaxSeconds = Number(Next(args, ref i));
                    break;
                case "--mark-seconds":
                    markSeconds = Number(Next(args, ref i));
                    break;
                case "--trace":
                    trace = true;
                    break;
                case "--seconds":
                    seconds = Number(Next(args, ref i));
                    break;
                case "--quality":
                    quality = QualityPresets.Parse(Next(args, ref i));
                    break;
                case "--fps":
                    fps = uint.Parse(Next(args, ref i));
                    break;
                case "--bitrate":
                    bitrate = uint.Parse(Next(args, ref i)) * 1000;
                    break;
                case "--out":
                    output = Next(args, ref i);
                    break;
                case "--window":
                    window = Next(args, ref i);
                    break;
                case "--shot-out":
                    shotOut = Next(args, ref i);
                    break;
                case "--keep-after-exit":
                    keepAfterExit = Number(Next(args, ref i));
                    break;
                case "--follow-larger":
                    followLarger = true;
                    break;
                case "--sample":
                    sampleOut = Next(args, ref i);
                    break;
                case "--recorder":
                    break;
                default:
                    throw new ArgumentException($"Неизвестный аргумент {args[i]}");
            }
        }

        output ??= $"clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";

        return new CommandLineOptions(
            probe,
            hotkeyProbe,
            audioProbe,
            audioMix,
            audioStream,
            audioMic,
            audioNoDesktop,
            noAudio,
            audioMeter,
            audioDenoiseFormats,
            audioDenoise,
            detectProbe,
            borderlessProbe,
            audioInput,
            audioNoiseSuppression,
            capture,
            audioKeepSeconds,
            audioRingSeconds,
            audioDevice,
            audioProcess,
            audioLevels,
            encoders,
            stream,
            buffer,
            watch,
            apps,
            supervised,
            trace,
            output,
            TimeSpan.FromSeconds(seconds),
            quality,
            fps,
            bitrate,
            segmentSeconds,
            bufferSeconds,
            bufferMegabytes,
            hotkey,
            mergeSeconds,
            mergeMaxSeconds,
            markSeconds,
            watchSeconds,
            graceSeconds,
            scanSeconds,
            windowSeconds,
            failDeviceSeconds,
            failDeviceTimes,
            failRestarts,
            clipsRoot,
            addApp,
            removeApp,
            window,
            shotOut,
            keepAfterExit,
            followLarger,
            sampleOut);
    }

    private static void Level(Dictionary<string, int> levels, string pair)
    {
        var split = pair.IndexOf('=');

        if (split <= 0 || !int.TryParse(pair[(split + 1)..], out var value))
        {
            throw new ArgumentException($"Громкость задаётся как --audio-level desktop=60, а не {pair}");
        }

        levels[pair[..split]] = value;
    }

    private static double Number(string value)
    {
        return double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static string Next(string[] args, ref int index)
    {
        index++;
        if (index >= args.Length)
        {
            throw new ArgumentException($"Не указано значение для {args[index - 1]}");
        }

        return args[index];
    }
}
