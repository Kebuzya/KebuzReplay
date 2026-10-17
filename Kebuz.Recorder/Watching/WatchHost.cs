using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Buffering;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Input;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Settings;

namespace Kebuz.Recorder.Watching;

internal sealed record WatchRequest(
    WatchedApps Apps,
    WatchSettings Settings,
    ReplaySessionSettings Video,
    KebuzSettings? Common,
    Hotkey Hotkey,
    TimeSpan Duration,
    Action<string>? Trace,
    bool Supervised = false,
    Action? Ready = null,
    Action<Action>? ExternalStop = null,
    Action<Action<KebuzSettings>>? SettingsChanged = null,
    Action<Action>? ExternalMark = null,
    Action<string, object>? Publish = null,
    Action<Action<bool>>? LevelWatch = null,
    bool MeterAlways = false,
    Action<Action>? ApplyNow = null,
    string? ShotOut = null);

internal static class WatchHost
{
    public static void Run(WatchRequest request)
    {
        var trace = request.Trace ?? (_ => { });

        if (request.Apps.Items.Count == 0)
        {
            if (!request.Supervised)
            {
                Log.Report($"Список отслеживаемых приложений пуст ({request.Apps.StoragePath})");
                Log.Flush();
                Console.WriteLine("Добавить: --add-app <exe или путь>");
                return;
            }

            Log.Report(
                $"Список отслеживаемых приложений пуст ({request.Apps.StoragePath}), " +
                "жду добавления, файл перечитывается на ходу");
        }
        else
        {
            Log.Report($"Отслеживаю: {string.Join(", ", request.Apps.Items.Select(app => app.Executable))}");
        }

        Log.Report($"Клипы: {Path.GetFullPath(request.Settings.ClipsRoot)}");
        Log.Report(
            $"Простой до закрытия сессии {request.Settings.Grace.TotalSeconds:F0} с, " +
            $"опрос процессов раз в {request.Settings.ScanInterval.TotalSeconds:F1} с");

        var video = request.Video;
        var peak = ReplayBuffer.EstimatePeakBytes(video.PeakBitrateBps, video.BufferLength, video.MergeMaxLength);
        var audioPeak = video.Audio is { AnySource: true }
            ? AudioBudget.RingBytes(video.BufferLength, video.MergeMaxLength)
            : 0;

        Log.Report(
            $"Качество: {video.Quality.Title}, " +
            $"{(video.Quality.TargetHeight is 0 ? "без масштабирования" : $"высота {video.Quality.TargetHeight}")}, " +
            $"{video.FrameRate ?? video.Quality.FrameRate} кадров, средний " +
            $"{(video.BitrateBps ?? video.Quality.MeanBitrateBps) / 1000d:F0} кбит/с, потолок {video.PeakBitrateBps / 1000d:F0} кбит/с");
        Log.Report(
            $"Пик памяти по расчёту: {Sizes.Format(peak + audioPeak)} " +
            $"(буфер {video.BufferLength.TotalSeconds:F0} с плюс потолок склейки {video.MergeMaxLength.TotalSeconds:F0} с" +
            $"{(audioPeak is 0 ? ", без звука" : $", из них звук {Sizes.Format(audioPeak)}")})");

        var publish = request.Publish ?? ((_, _) => { });
        var microphoneLost = false;

        void Report(ScheduledClip clip)
        {
            Log.Report(
                $"Клип: {clip.Result.Path}, меток {clip.Marks}, {clip.Result.Duration.TotalSeconds:F3} с, " +
                $"{Sizes.Format(clip.Result.FileSizeBytes)}, сохранение {clip.Elapsed.TotalMilliseconds:F0} мс");

            publish(PipeMessages.ClipSaved, Describe(clip, microphoneLost));
        }

        void Health(SourceHealth source)
        {
            if (source.Kind is "mic")
            {
                microphoneLost = !source.Live;
            }

            Log.Report(source.Live
                ? $"Звук: {source.Title} на месте"
                : $"Звук: {source.Title} недоступен, {source.Complaint}");

            publish(PipeMessages.AudioSourceChanged, source);
        }

        long levelMessages = 0;

        AudioMeter.Attach(levels =>
        {
            Interlocked.Increment(ref levelMessages);
            publish(PipeMessages.AudioLevels, levels);
        });

        var preview = new AudioPreview(request.Video.Audio ?? AudioTrackSettings.Of(AudioSettings.Default), trace);

        void Levels(bool on)
        {
            Log.Report(on ? "Индикатор уровня включён" : "Индикатор уровня выключен");
            AudioMeter.Watch(on);
            preview.Watch(on);
        }

        void State(RecordingState next)
        {
            preview.Recording(next.Recording);

            if (!next.Recording)
            {
                microphoneLost = false;
            }

            publish(PipeMessages.RecordingStateChanged, next);
        }

        using var exit = new ManualResetEventSlim(false);

        GlobalHotkey? hotkey = null;

        var hotkeyGate = new Lock();
        var commonHotkey = request.Hotkey;
        Hotkey? sessionHotkey = null;

        void Rebind()
        {
            Hotkey next;

            lock (hotkeyGate)
            {
                next = sessionHotkey ?? commonHotkey;
            }

            hotkey?.Rebind(next);
        }

        void Session(string? combination)
        {
            Hotkey? parsed = null;

            if (combination is not null)
            {
                try
                {
                    parsed = Hotkey.Parse(combination);
                }
                catch (ArgumentException ex)
                {
                    Log.Report($"Сочетание профиля {combination} не разобрано: {ex.Message}");
                }
            }

            lock (hotkeyGate)
            {
                sessionHotkey = parsed;
            }

            Log.Report(parsed is null
                ? "Хоткей вернулся к общему"
                : $"Хоткей профиля: {parsed}");

            Rebind();
        }

        var director = new SessionDirector(
            request.Apps,
            request.Settings,
            request.Video,
            request.Common,
            Report,
            Log.Report,
            message => trace($"сессия: {message}"),
            State,
            bytes => publish(PipeMessages.BufferMemoryUsage, new BufferUsage(bytes)),
            reason => publish(PipeMessages.CaptureRecovered, new RecoveryInfo(reason)),
            Health,
            _ => exit.Set(),
            Session,
            game => publish(PipeMessages.GameDetected, game),
            shot => Shown(shot));

        void Shown(CaptureShot shot)
        {
            Log.Report($"Пишется окно: {shot.Game} · {shot.Window} {shot.Width}x{shot.Height}");
            publish(PipeMessages.CaptureShown, shot);

            if (request.ShotOut is null)
            {
                return;
            }

            try
            {
                File.WriteAllBytes(request.ShotOut, shot.Png);
            }
            catch (Exception ex)
            {
                Log.Report($"Кадр окна не записан в {request.ShotOut}: {ex.Message}");
            }
        }

        void Bound(Hotkey combination, string? complaint)
        {
            Log.Report(complaint is null
                ? $"Хоткей {combination} ставит метку"
                : $"Хоткей {combination} не зарегистрирован: {Localization.Strings.Render(complaint)}. Метку ставит кнопка в галерее");

            publish(PipeMessages.HotkeyChanged, new HotkeyState(combination.ToString(), complaint is null, complaint));
        }

        hotkey = new GlobalHotkey(request.Hotkey, director.Trigger, Bound);

        Rebind();

        request.SettingsChanged?.Invoke(next =>
        {
            director.Apply(
                request.Settings with { ClipsRoot = next.EffectiveClipsRoot },
                request.Video.Apply(next),
                next);

            preview.Apply(request.Video.Apply(next).Audio ?? AudioTrackSettings.Of(AudioSettings.Default));

            Log.Report($"Новые настройки приняты, запись со следующей сессии: {next}");

            try
            {
                lock (hotkeyGate)
                {
                    commonHotkey = Hotkey.Parse(next.Hotkey);
                }
            }
            catch (ArgumentException ex)
            {
                Log.Report($"Сочетание {next.Hotkey} не разобрано, прежний хоткей остаётся: {ex.Message}");
                return;
            }

            Rebind();
        });

        request.ExternalMark?.Invoke(director.Trigger);
        request.LevelWatch?.Invoke(Levels);
        request.ApplyNow?.Invoke(director.ApplyNow);

        if (request.MeterAlways)
        {
            Levels(true);
        }

        Log.Flush();

        request.Ready?.Invoke();
        request.ExternalStop?.Invoke(exit.Set);

        if (request.Duration > TimeSpan.Zero)
        {
            Console.WriteLine($"Наблюдение {request.Duration.TotalSeconds:F0} с");
            exit.Wait(request.Duration);
        }
        else
        {
            Console.WriteLine("Наблюдение до Ctrl+C");
            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                exit.Set();
            };

            exit.Wait();
        }

        hotkey.Dispose();
        preview.Dispose();
        director.Dispose();
        AudioMeter.Watch(false);
        AudioMeter.Attach(null);

        Log.Flush();

        Console.WriteLine($"Сессий за прогон: {director.Sessions}, перезапусков записи: {director.Restarts}");

        if (request.MeterAlways)
        {
            Console.WriteLine($"Сообщений уровня: {Interlocked.Read(ref levelMessages)}");
        }

        if (hotkey.Failure is not null)
        {
            throw new InvalidOperationException("Обработчик хоткея упал", hotkey.Failure);
        }

        if (director.Failure is not null)
        {
            throw new InvalidOperationException("Наблюдение за процессами прервано", director.Failure);
        }
    }

    private static ClipInfo Describe(ScheduledClip clip, bool withoutMicrophone)
    {
        var session = Path.GetDirectoryName(clip.Result.Path) ?? string.Empty;
        var game = Path.GetFileName(Path.GetDirectoryName(session) ?? string.Empty);

        return new ClipInfo(
            clip.Result.Path,
            clip.Result.Duration.TotalSeconds,
            clip.Result.FileSizeBytes,
            session,
            game,
            withoutMicrophone);
    }
}
