using System.Diagnostics;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Input;

namespace Kebuz.Recorder.Buffering;

internal sealed record ReplayBufferProbeRequest(
    string OutputPath,
    TimeSpan Duration,
    TimeSpan SegmentLength,
    TimeSpan BufferLength,
    long MaxBytes,
    QualityProfile Quality,
    uint? FrameRate,
    uint? BitrateBps,
    Hotkey Hotkey,
    TimeSpan MergeDelay,
    TimeSpan MergeMaxLength,
    AudioTrackSettings? Audio,
    TimeSpan MarkAfter,
    Action<string>? Trace = null);

internal static class ReplayBufferProbe
{
    public static void Run(ReplayBufferProbeRequest request)
    {
        var trace = request.Trace ?? (_ => { });

        using var device = GraphicsDevice.Create();
        var item = CaptureTarget.PrimaryMonitor();

        var video = VideoEncoderSettings.Create(
            (uint)item.Size.Width,
            (uint)item.Size.Height,
            request.Quality,
            request.FrameRate,
            request.BitrateBps);

        var settings = new ReplayBufferSettings(
            request.SegmentLength,
            request.BufferLength,
            request.MaxBytes,
            request.MergeMaxLength,
            request.Audio);

        using var buffer = new ReplayBuffer(device, video, settings, trace);

        Console.WriteLine($"Энкодер: {buffer.Name}");
        Console.WriteLine($"Пресет: {request.Quality.Title}");
        Console.WriteLine(
            $"Поток {video.SourceWidth}x{video.SourceHeight} в {video.Width}x{video.Height} @ {video.FrameRate}, " +
            $"{request.Duration.TotalSeconds:F0} с");
        Console.WriteLine(
            $"Средний {video.BitrateBps / 1000d:F0} кбит/с, потолок {video.PeakBitrateBps / 1000d:F0} кбит/с");
        Console.WriteLine($"Управление: {buffer.Controls}, вход {buffer.InputFormat}, масштабирование {buffer.Scaling}");
        Console.WriteLine(
            $"Буфер: сегмент {request.SegmentLength.TotalSeconds:F1} с, длина {request.BufferLength.TotalSeconds:F0} с, " +
            $"лимит памяти {Sizes.Format(request.MaxBytes)}");
        var peak = ReplayBuffer.EstimatePeakBytes(video.PeakBitrateBps, request.BufferLength, request.MergeMaxLength);
        var audioPeak = buffer.Audio is null
            ? 0
            : AudioBudget.RingBytes(request.BufferLength, request.MergeMaxLength);

        Console.WriteLine(
            $"Пик памяти по расчёту: {Sizes.Format(peak + audioPeak)} " +
            $"(буфер {request.BufferLength.TotalSeconds:F0} с плюс потолок склейки {request.MergeMaxLength.TotalSeconds:F0} с" +
            $"{(audioPeak is 0 ? ", без звука" : $", из них звук {Sizes.Format(audioPeak)}")})");

        if (buffer.Audio is { } track)
        {
            Console.WriteLine($"Звук: {track.Name}, {AudioEncoder.BitrateBps / 1000d:F0} кбит/с");

            foreach (var source in track.Sources)
            {
                Console.WriteLine(
                    $"  {source.Title}: " +
                    $"{(source.State is AudioSourceState.Live ? source.DeviceName : Localization.Strings.Render(source.Complaint))}");
            }
        }
        else
        {
            Console.WriteLine("Звук: нет");
        }

        static void Report(ScheduledClip clip)
        {
            Console.WriteLine(
                $"Клип {clip.Index}: {clip.Result.Path}, меток {clip.Marks}, сэмплов {clip.Result.Samples}, " +
                $"ключевых {clip.Result.KeyFrames}, {clip.Result.Duration.TotalSeconds:F3} с, " +
                $"звука {clip.Result.AudioFrames} фреймов, {clip.Result.AudioDuration.TotalSeconds:F3} с, " +
                $"{Sizes.Format(clip.Result.FileSizeBytes)}, " +
                $"сохранение {clip.Elapsed.TotalMilliseconds:F0} мс");
        }

        var scheduler = new ClipScheduler(
            buffer,
            new ClipMergeSettings(request.MergeDelay, request.MergeMaxLength),
            (index, _) => NumberedPath(request.OutputPath, index),
            Report,
            message => trace($"клип: {message}"));

        using var hotkey = new GlobalHotkey(request.Hotkey, scheduler.Trigger);

        Console.WriteLine(
            $"Хоткей {request.Hotkey} ставит метку, клип пишется после {request.MergeDelay.TotalSeconds:F1} с " +
            $"тишины, склейка не длиннее {request.MergeMaxLength.TotalSeconds:F0} с");

        using var marker = request.MarkAfter > TimeSpan.Zero
            ? new Timer(_ => scheduler.Trigger(), null, request.MarkAfter, Timeout.InfiniteTimeSpan)
            : null;

        if (marker is not null)
        {
            Console.WriteLine($"Метка ставится сама через {request.MarkAfter.TotalSeconds:F0} с после старта");
        }

        var result = CaptureLoop.Run(
            device,
            item,
            buffer,
            new CaptureLoopRequest(
                video.SourceWidth,
                video.SourceHeight,
                video.FrameRate,
                request.Duration,
                trace));

        hotkey.Dispose();
        scheduler.Dispose();

        var status = buffer.Status;

        Log.Flush();

        Console.WriteLine(
            $"Кадров захвачено: {result.FramesCaptured}, отдано энкодеру: {result.FramesWritten}, отброшено: {result.FramesDropped}");
        Console.WriteLine(
            $"Буфер: сегментов {status.Segments}, {status.Duration.TotalSeconds:F3} с, сэмплов {status.Samples}, " +
            $"данных {Sizes.Format(status.PayloadBytes)} в {Sizes.Format(status.Bytes)}, " +
            $"до первого ключевого пропущено {status.Skipped}");

        if (buffer.Audio is { } sound)
        {
            var audio = sound.Status;

            Console.WriteLine(
                $"Микшер: блоков {sound.Blocks}, пик {sound.Peak:F4}, срезано {sound.Clipped}, " +
                $"фреймов AAC {sound.Frames}, данных {Sizes.Format(sound.Bytes)}");
            Console.WriteLine(
                $"Аудиокольцо: фреймов {audio.Frames}, занято {Sizes.Format(audio.Bytes)} из " +
                $"{Sizes.Format(audio.Capacity)}, звука {audio.Duration.TotalSeconds:F3} с, " +
                $"обрезано {audio.Trimmed}, переполнений {audio.Overrun}");
        }

        if (status.Duration > TimeSpan.Zero)
        {
            Console.WriteLine(
                $"Фактический поток в буфере: {status.PayloadBytes * 8d / status.Duration.TotalSeconds / 1_000_000d:F2} Мбит/с");
        }

        using var self = Process.GetCurrentProcess();
        Console.WriteLine(
            $"Память процесса: рабочий набор {Sizes.Format(self.WorkingSet64)}, " +
            $"пик {Sizes.Format(self.PeakWorkingSet64)}, private {Sizes.Format(self.PrivateMemorySize64)}");

        if (hotkey.Failure is not null)
        {
            throw new InvalidOperationException("Обработчик хоткея упал", hotkey.Failure);
        }

        if (scheduler.Failure is not null)
        {
            throw new InvalidOperationException("Сохранение клипа не удалось", scheduler.Failure);
        }

        Console.WriteLine(scheduler.Marks == 0
            ? "Хоткей не нажимался, клипы не сохранялись"
            : $"Сохранено клипов: {scheduler.Clips}, меток {scheduler.Marks}");
    }

    private static string NumberedPath(string path, int index)
    {
        if (index <= 1)
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        var name = $"{Path.GetFileNameWithoutExtension(path)}_{index}{Path.GetExtension(path)}";

        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }
}
