using System.Diagnostics;
using Kebuz.Recorder.Buffering;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Settings;

namespace Kebuz.Recorder.Watching;

internal sealed record WatchSettings(
    string ClipsRoot,
    TimeSpan Grace,
    TimeSpan ScanInterval,
    TimeSpan WindowTimeout,
    TimeSpan FailDeviceAfter = default,
    int FailDeviceTimes = 1,
    int FailRestarts = 0,
    TimeSpan? KeepAfterExit = null,
    TimeSpan MarkAt = default,
    bool? FollowLarger = null);

internal sealed class SessionDirector : IDisposable
{
    private const int WindowSettleScans = 2;
    private const int RecoveryLimit = 3;

    private static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecoveryDeadline = TimeSpan.FromSeconds(30);

    private WatchedApps apps;
    private WatchSettings settings;
    private ReplaySessionSettings video;
    private KebuzSettings? common;

    private readonly Action<ScheduledClip> saved;
    private readonly Action<string> report;
    private readonly Action<string> trace;
    private readonly Action<RecordingState> state;
    private readonly Action<long> memory;
    private readonly Action<string> recovered;
    private readonly Action<SourceHealth> health;
    private readonly Action<Exception> fatal;
    private readonly Action<string?> rebind;
    private readonly Action<DetectedGame> detected;
    private readonly Action<CaptureShot> shown;
    private readonly GameDetector detector = new();

    private readonly List<long> recoveries = [];
    private readonly List<RunningProcess> processes = [];
    private readonly Lock gate = new();
    private readonly ManualResetEventSlim wake = new(false);
    private readonly Thread thread;

    private ReplaySession? session;
    private WatchedApp? targetApp;
    private uint targetId;
    private uint abandonedId;
    private nint candidateWindow;
    private int candidateScans;
    private long waitingSince;
    private WatchedApp? folderApp;
    private string? folder;
    private long idleSince;
    private int sessions;
    private int restarts;
    private int clipsInRun;
    private bool continuing;
    private Exception? failure;
    private bool stopping;
    private bool disposed;
    private WatchSettings? nextSettings;
    private ReplaySessionSettings? nextVideo;
    private KebuzSettings? nextCommon;
    private string? boundHotkey;
    private DateTime appsStamp;
    private IgnoredApps ignored;
    private DateTime ignoredStamp;
    private long reportedMemory;
    private bool recovering;
    private bool standardOnly;
    private bool applyRequested;
    private bool pending;
    private KebuzSettings? sessionCommon;
    private RecordingState? lastState;
    private string? trouble;
    private long recoverySince;
    private int simulationsLeft;
    private int failedRestartsLeft;
    private WatchedApp? afterApp;
    private long afterSince;
    private TimeSpan afterLength;
    private readonly long born = Stopwatch.GetTimestamp();
    private bool marked;
    private nint followCandidate;
    private int followScans;

    public SessionDirector(
        WatchedApps apps,
        WatchSettings settings,
        ReplaySessionSettings video,
        KebuzSettings? common,
        Action<ScheduledClip> saved,
        Action<string> report,
        Action<string> trace,
        Action<RecordingState> state,
        Action<long> memory,
        Action<string>? recovered = null,
        Action<SourceHealth>? health = null,
        Action<Exception>? fatal = null,
        Action<string?>? rebind = null,
        Action<DetectedGame>? detected = null,
        Action<CaptureShot>? shown = null)
    {
        this.apps = apps;
        this.settings = settings;
        this.video = video;
        this.common = common;
        this.saved = saved;
        this.report = report;
        this.trace = trace;
        this.state = state;
        this.memory = memory;
        this.recovered = recovered ?? (_ => { });
        this.health = health ?? (_ => { });
        this.fatal = fatal ?? (_ => { });
        this.rebind = rebind ?? (_ => { });
        this.detected = detected ?? (_ => { });
        this.shown = shown ?? (_ => { });

        simulationsLeft = settings.FailDeviceAfter > TimeSpan.Zero ? settings.FailDeviceTimes : 0;
        appsStamp = Stamp(apps.StoragePath);
        ignored = LoadIgnored() ?? IgnoredApps.Empty;
        ignoredStamp = Stamp(ignored.StoragePath);

        thread = new Thread(Loop)
        {
            Name = "KebuzSessionDirector",
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public int Sessions => Volatile.Read(ref sessions);

    public int Restarts => Volatile.Read(ref restarts);

    public Exception? Failure => Volatile.Read(ref failure);

    public void Apply(WatchSettings next, ReplaySessionSettings nextSession, KebuzSettings? nextSettingsCommon)
    {
        lock (gate)
        {
            nextSettings = next;
            nextVideo = nextSession;
            nextCommon = nextSettingsCommon;
        }
    }

    public void ApplyNow()
    {
        lock (gate)
        {
            applyRequested = true;
        }
    }

    public void Trigger()
    {
        lock (gate)
        {
            if (session is null)
            {
                trace("хоткей нажат, но сессия не активна");
                return;
            }

            session.Trigger();
        }
    }

    private void Loop()
    {
        while (!Stopping())
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                trace($"наблюдатель упал: {ex.Message}");
                DropSession();
            }

            if (Stopping())
            {
                break;
            }

            wake.Wait(settings.ScanInterval);
        }

        Close();
    }

    private bool Stopping()
    {
        lock (gate)
        {
            return stopping;
        }
    }

    private void Tick()
    {
        Adopt();
        Reload();
        ReloadIgnored();

        ProcessScanner.Snapshot(processes);

        if (abandonedId != 0 && !Alive(abandonedId))
        {
            abandonedId = 0;
        }

        if (targetApp is not null && !Alive(targetId))
        {
            LoseTarget();
        }

        Expire();
        MarkOnSchedule();

        if (targetApp is null)
        {
            AcquireTarget();
        }

        if (targetApp is null && (common?.DetectGames ?? true))
        {
            detector.Tick(apps, ignored, Found);
        }
        else
        {
            detector.Forget();
        }

        Inspect();
        Follow();
        Restart();

        if (targetApp is not null && (session is null || afterApp is not null))
        {
            TryStart();
        }

        Compare();

        Meter();
        Retarget();

        if (targetApp is null && session is null && folder is not null && Stopwatch.GetElapsedTime(idleSince) > settings.Grace)
        {
            report($"сессия закрыта: {folder}");
            folder = null;
            folderApp = null;
        }
    }

    private void Adopt()
    {
        WatchSettings? incoming;
        ReplaySessionSettings? incomingVideo;
        KebuzSettings? incomingCommon;

        lock (gate)
        {
            incoming = nextSettings;
            incomingVideo = nextVideo;
            incomingCommon = nextCommon;
            nextSettings = null;
            nextVideo = null;
            nextCommon = null;
        }

        if (incoming is null || incomingVideo is null)
        {
            return;
        }

        settings = incoming;
        video = incomingVideo;
        common = incomingCommon ?? common;
    }

    private void Restart()
    {
        bool requested;

        lock (gate)
        {
            requested = applyRequested;
            applyRequested = false;
        }

        if (!requested)
        {
            return;
        }

        if (standardOnly)
        {
            standardOnly = false;
            report("без рамки снова разрешён для этой игры");
        }

        var closing = Detach();

        if (closing is null)
        {
            report("применить настройки сейчас: записи нет, применятся при следующем запуске");
            return;
        }

        waitingSince = Stopwatch.GetTimestamp();

        try
        {
            var result = closing.Stop();
            report(
                $"запись перезапускается с новыми настройками: кадров {result.FramesCaptured}, " +
                $"клипов {Tally(closing)}, буфер сброшен");
        }
        catch (Exception ex)
        {
            report($"остановка записи перед перезапуском не удалась: {ex.Message}");
        }
        finally
        {
            closing.Dispose();
        }
    }

    private void Compare()
    {
        ReplaySession? current;

        lock (gate)
        {
            current = session;
        }

        var now = current is not null
            && common is not null
            && sessionCommon is not null
            && targetApp is not null
            && targetApp.Overrides.Apply(common).Recorded() != sessionCommon.Recorded();

        if (now == pending)
        {
            return;
        }

        pending = now;

        if (current is not null && lastState is { } shown)
        {
            Publish(shown with { SettingsPending = pending });
        }
    }

    private void Publish(RecordingState next)
    {
        lastState = next.Recording ? next : null;
        state(next);
    }

    private void Retarget()
    {
        var wanted = Wanted();

        if (string.Equals(wanted, boundHotkey, StringComparison.Ordinal))
        {
            return;
        }

        boundHotkey = wanted;
        rebind(wanted);
    }

    private string? Wanted()
    {
        if (common is null || (targetApp ?? afterApp) is not { } current)
        {
            return null;
        }

        var profile = Listed(current).Overrides;

        if (profile.Hotkey is null)
        {
            return null;
        }

        var merged = profile.Apply(common).Hotkey;

        return string.Equals(merged, common.Hotkey, StringComparison.Ordinal) ? null : merged;
    }

    private WatchedApp Listed(WatchedApp app)
    {
        return apps.Find(app.Executable, app.Path) ?? app;
    }

    private void Reload()
    {
        var stamp = Stamp(apps.StoragePath);

        if (stamp == appsStamp)
        {
            return;
        }

        WatchedApps reloaded;

        try
        {
            reloaded = WatchedApps.Load(apps.StoragePath);
        }
        catch (Exception ex)
        {
            trace($"список приложений не перечитался: {ex.Message}");
            return;
        }

        apps = reloaded;
        appsStamp = stamp;

        report(reloaded.Items.Count == 0
            ? "список приложений опустел"
            : $"список приложений перечитан: {string.Join(", ", reloaded.Items.Select(app => app.Executable))}");

        if (targetApp is not null && reloaded.Find(targetApp.Executable, targetApp.Path) is null)
        {
            Unlist();
        }
    }

    private void Unlist()
    {
        var app = targetApp!;

        targetApp = null;
        recovering = false;
        trouble = null;

        var closing = Detach();

        if (closing is not null)
        {
            try
            {
                var result = closing.Stop();

                report(
                    $"{app.Title} убран из списка, запись остановлена: кадров {result.FramesCaptured}, " +
                    $"отброшено {result.FramesDropped}, клипов {Tally(closing)}");
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                report($"остановка записи не удалась: {ex.Message}");
            }
            finally
            {
                closing.Dispose();
            }
        }
        else
        {
            report($"{app.Title} убран из списка до старта записи");
        }

        EndRun();

        if (folder is not null)
        {
            report($"сессия закрыта: {folder}");
            folder = null;
            folderApp = null;
        }
    }

    private void Found(DetectedGame game)
    {
        report($"похоже на игру: {game.Title} ({game.Path}), спрашиваю");
        detected(game);
    }

    private void ReloadIgnored()
    {
        var stamp = Stamp(ignored.StoragePath);

        if (stamp == ignoredStamp)
        {
            return;
        }

        if (LoadIgnored() is not { } fresh)
        {
            return;
        }

        ignored = fresh;
        ignoredStamp = stamp;
        report($"список не-игр перечитан: {ignored.Items.Count}");
    }

    private IgnoredApps? LoadIgnored()
    {
        try
        {
            return IgnoredApps.Load();
        }
        catch (Exception ex)
        {
            trace($"список не-игр не прочитан: {ex.Message}");
            return null;
        }
    }

    private static DateTime Stamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private bool Alive(uint id)
    {
        foreach (var process in processes)
        {
            if (process.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    private void AcquireTarget()
    {
        foreach (var process in processes)
        {
            if (process.Id == abandonedId)
            {
                continue;
            }

            var app = apps.Match(process.Executable, () => ProcessScanner.TryGetPath(process.Id));
            if (app is null)
            {
                continue;
            }

            if (afterApp is not null && !string.Equals(afterApp.Executable, app.Executable, StringComparison.OrdinalIgnoreCase))
            {
                EndAfterlife($"запущен {app.Title}");
            }

            targetApp = app;
            standardOnly = false;
            targetId = process.Id;
            candidateWindow = 0;
            candidateScans = 0;
            waitingSince = Stopwatch.GetTimestamp();

            if (folder is not null && folderApp is not null && !string.Equals(
                    folderApp.Executable,
                    app.Executable,
                    StringComparison.OrdinalIgnoreCase))
            {
                report($"сессия закрыта: {folder}");
                folder = null;
                folderApp = null;
            }

            if (folder is null)
            {
                folder = SessionPaths.SessionFolder(settings.ClipsRoot, app.Title, DateTime.Now);
                folderApp = app;
                report($"{app.Title} запущен (pid {process.Id}), сессия {folder}");
            }
            else
            {
                report($"{app.Title} вернулся (pid {process.Id}) в пределах простоя, сессия та же");
            }

            return;
        }
    }

    private void Inspect()
    {
        ReplaySession? current;

        lock (gate)
        {
            current = session;
        }

        if (current is null || current.Trouble() is not { } found)
        {
            return;
        }

        report($"запись прервана: {Localization.Strings.Render(found)}");

        if (current.Borderless && Localization.Strings.KeyOf(found) == ReplaySession.BorderlessTrouble)
        {
            standardOnly = true;
            report("дальше эта игра пишется стандартным WGC");
        }

        Detach();
        waitingSince = Stopwatch.GetTimestamp();

        try
        {
            current.Abort();
            Tally(current);
        }
        finally
        {
            current.Dispose();
        }

        recovering = true;
        trouble = found;
        recoverySince = Stopwatch.GetTimestamp();
        failedRestartsLeft = settings.FailRestarts;

        if (!Note(Localization.Strings.Render(found)))
        {
            recovering = false;
            targetApp = null;
        }
    }

    private bool Note(string reason)
    {
        recoveries.RemoveAll(stamp => Stopwatch.GetElapsedTime(stamp) > RecoveryWindow);
        recoveries.Add(Stopwatch.GetTimestamp());

        if (recoveries.Count < RecoveryLimit)
        {
            return true;
        }

        var ex = new InvalidOperationException(
            $"Захват не восстанавливается: {recoveries.Count} отказа за {RecoveryWindow.TotalSeconds:F0} с, " +
            $"последний: {reason}");

        Interlocked.CompareExchange(ref failure, ex, null);
        report($"{ex.Message}. Останавливаюсь, дальше поднимает watchdog");
        fatal(ex);

        return false;
    }

    private void TryStart()
    {
        var hwnd = GameWindow.FindLargest(targetId);

        if (hwnd == 0)
        {
            if (Stopwatch.GetElapsedTime(waitingSince) > settings.WindowTimeout)
            {
                report($"окно {targetApp!.Title} не появилось за {settings.WindowTimeout.TotalSeconds:F0} с, процесс пропущен");
                abandonedId = targetId;
                targetApp = null;
            }

            return;
        }

        if (hwnd != candidateWindow)
        {
            candidateWindow = hwnd;
            candidateScans = 1;
            trace($"кандидат в окна 0x{hwnd:X}, жду подтверждения");
            return;
        }

        if (++candidateScans < WindowSettleScans)
        {
            return;
        }

        if (afterApp is not null && session is not null)
        {
            Resume(hwnd);
            return;
        }

        ReplaySession started;

        var profile = targetApp!.Overrides;
        var sessionVideo = common is null ? video : video.Apply(profile.Apply(common));
        var asked = sessionVideo.Capture;

        if (standardOnly)
        {
            sessionVideo = sessionVideo with { Capture = CaptureMode.Standard };
        }

        try
        {
            Directory.CreateDirectory(settings.ClipsRoot);

            if (recovering && failedRestartsLeft > 0)
            {
                failedRestartsLeft--;
                throw new InvalidOperationException("пересоздание устройства провалено намеренно");
            }

            var simulate = TimeSpan.Zero;

            if (simulationsLeft > 0)
            {
                simulate = settings.FailDeviceAfter;
                simulationsLeft--;
                trace($"устройство будет объявлено потерянным через {simulate.TotalSeconds:F0} с");
            }

            started = ReplaySession.Start(hwnd, targetId, folder!, sessionVideo, saved, trace, health, simulate, Shot(hwnd, targetApp!.Title));
        }
        catch (Exception ex)
        {
            if (recovering)
            {
                var waited = Stopwatch.GetElapsedTime(recoverySince);

                if (waited <= RecoveryDeadline)
                {
                    trace($"захват не поднялся ({ex.Message}), жду, прошло {waited.TotalSeconds:F0} с");
                    return;
                }

                report($"захват не поднялся за {RecoveryDeadline.TotalSeconds:F0} с: {ex.Message}");
                recovering = false;
                targetApp = null;

                var dead = new InvalidOperationException(
                    $"Захват не восстановился за {RecoveryDeadline.TotalSeconds:F0} с: {ex.Message}", ex);

                Interlocked.CompareExchange(ref failure, dead, null);
                report("Останавливаюсь, дальше поднимает watchdog");
                fatal(dead);
                return;
            }

            Interlocked.CompareExchange(ref failure, ex, null);
            report($"запись {targetApp!.Title} не стартовала: {ex.Message}");
            abandonedId = targetId;
            targetApp = null;
            EndRun();
            return;
        }

        lock (gate)
        {
            session = started;
        }

        Interlocked.Increment(ref continuing ? ref restarts : ref sessions);
        continuing = true;

        if (profile.Any)
        {
            report($"профиль {targetApp!.Title}: {profile}");
        }

        report(
            $"запись пошла: {started.Geometry} @ {started.Video.FrameRate}, энкодер {started.Encoder}, " +
            $"{started.Controls}, захват {started.Capture}, буфер {sessionVideo.BufferLength.TotalSeconds:F0} с, " +
            $"окно тишины {sessionVideo.MergeDelay.TotalSeconds:F0} с, звук {started.Sound}");

        reportedMemory = 0;
        sessionCommon = common is null ? null : profile.Apply(common);
        pending = false;
        Publish(new RecordingState(
            true,
            targetApp!.Title,
            folder,
            profile.Any,
            sessionVideo.BufferLength.TotalSeconds,
            Wanted(),
            CaptureNote(asked, started)));

        if (recovering)
        {
            recovering = false;
            report($"захват восстановлен в той же сессии: {folder}");
            recovered(trouble ?? Localization.Strings.Code("Trouble.Unknown"));
            trouble = null;
        }
    }

    private void Resume(nint hwnd)
    {
        var app = targetApp!;
        var current = session!;
        var profile = app.Overrides;
        var sessionVideo = common is null ? video : video.Apply(profile.Apply(common));
        var asked = sessionVideo.Capture;
        var mode = standardOnly ? CaptureMode.Standard : asked;

        try
        {
            current.Reattach(hwnd, targetId, mode, Shot(hwnd, app.Title));
        }
        catch (Exception ex)
        {
            report($"окно {app.Title} не подхватилось в ту же запись ({ex.Message}), начинаю новую");
            EndAfterlife("окно не подхватилось");
            return;
        }

        var paused = Stopwatch.GetElapsedTime(afterSince);
        afterApp = null;

        report($"{app.Title} вернулся через {paused.TotalSeconds:F0} с, запись продолжается без разрыва, захват {current.Capture}");

        Publish(new RecordingState(
            true,
            app.Title,
            folder,
            profile.Any,
            lastState?.BufferSeconds ?? sessionVideo.BufferLength.TotalSeconds,
            Wanted(),
            CaptureNote(asked, current),
            pending));
    }

    private Action<ShotImage> Shot(nint hwnd, string game)
    {
        var window = CaptureTarget.GetWindowTitle(new Windows.Win32.Foundation.HWND(hwnd));

        return image =>
        {
            try
            {
                trace($"кадр окна снят: {image.Width}x{image.Height}, png {image.Png.Length} байт{(image.Dark ? ", тёмный" : string.Empty)}");
                shown(new CaptureShot(game, window, image.SourceWidth, image.SourceHeight, image.Png, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                trace($"кадр окна не отправлен: {ex.Message}");
            }
        };
    }

    private string? CaptureNote(CaptureMode asked, ReplaySession started)
    {
        if (asked is not CaptureMode.Borderless)
        {
            return null;
        }

        if (standardOnly)
        {
            return RecordingState.BorderlessFailedNote;
        }

        return started.Borderless ? RecordingState.BorderlessNote : RecordingState.BorderlessNotRaisedNote;
    }

    private void Follow()
    {
        var wanted = settings.FollowLarger ?? common?.FollowLargerWindow ?? false;

        if (!wanted || targetApp is null || afterApp is not null || session is not { } current)
        {
            followCandidate = 0;
            return;
        }

        var hwnd = GameWindow.FindLargest(targetId);
        var area = GameWindow.Area(hwnd);
        var held = GameWindow.Area(current.Window);

        if (hwnd == 0 || hwnd == current.Window || area <= held)
        {
            followCandidate = 0;
            return;
        }

        if (hwnd != followCandidate)
        {
            followCandidate = hwnd;
            followScans = 1;
            trace($"окно больше пишущегося: 0x{hwnd:X}, площадь {area} против {held}, жду подтверждения");
            return;
        }

        if (++followScans < WindowSettleScans)
        {
            return;
        }

        followCandidate = 0;
        report($"у {targetApp.Title} появилось окно больше пишущегося, перезапускаю запись на нём, буфер сброшен");

        var closing = Detach();
        waitingSince = Stopwatch.GetTimestamp();
        candidateWindow = hwnd;
        candidateScans = WindowSettleScans - 1;

        if (closing is null)
        {
            return;
        }

        try
        {
            var result = closing.Stop();
            report($"запись на прежнем окне остановлена: кадров {result.FramesCaptured}, клипов {Tally(closing)}");
        }
        catch (Exception ex)
        {
            report($"остановка записи перед переходом на большее окно не удалась: {ex.Message}");
        }
        finally
        {
            closing.Dispose();
        }
    }

    private void MarkOnSchedule()
    {
        if (marked || settings.MarkAt <= TimeSpan.Zero || Stopwatch.GetElapsedTime(born) < settings.MarkAt)
        {
            return;
        }

        marked = true;
        report($"метка по расписанию на {settings.MarkAt.TotalSeconds:F0} с");
        Trigger();
    }

    private TimeSpan? KeepAfterExit()
    {
        if (settings.KeepAfterExit is { } forced)
        {
            return forced > TimeSpan.Zero ? forced : null;
        }

        var keep = common ?? KebuzSettings.Default;

        return keep.KeepAfterExit && keep.KeepAfterExitSeconds > 0
            ? TimeSpan.FromSeconds(keep.KeepAfterExitSeconds)
            : null;
    }

    private void Expire()
    {
        if (afterApp is null)
        {
            return;
        }

        if (Stopwatch.GetElapsedTime(afterSince) <= afterLength)
        {
            return;
        }

        EndAfterlife($"прошло {afterLength.TotalSeconds:F0} с");
    }

    private void EndAfterlife(string why)
    {
        var app = afterApp;

        if (app is null)
        {
            return;
        }

        var closing = Detach();
        idleSince = Stopwatch.GetTimestamp();

        if (closing is null)
        {
            return;
        }

        try
        {
            var result = closing.Stop();

            report(
                $"запись после выхода {app.Title} остановлена ({why}): кадров окна {result.FramesCaptured}, " +
                $"закодировано {result.FramesWritten}, клипов {Tally(closing)}");
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref failure, ex, null);
            report($"остановка записи не удалась: {ex.Message}");
        }
        finally
        {
            closing.Dispose();
        }

        EndRun();
        report($"жду возврата {app.Title} ещё {settings.Grace.TotalSeconds:F0} с");
    }

    private void LoseTarget()
    {
        var app = targetApp!;

        targetApp = null;
        recovering = false;
        trouble = null;
        idleSince = Stopwatch.GetTimestamp();

        if (session is { } living && KeepAfterExit() is { } keep)
        {
            try
            {
                living.Blackout();
            }
            catch (Exception ex)
            {
                report($"перейти на чёрный кадр не удалось: {ex.Message}");
                keep = TimeSpan.Zero;
            }

            if (keep > TimeSpan.Zero)
            {
                afterApp = app;
                afterSince = Stopwatch.GetTimestamp();
                afterLength = keep;

                var until = DateTime.Now + keep;
                report($"{app.Title} завершился, пишу чёрный кадр со звуком ещё {keep.TotalSeconds:F0} с, хоткей работает");

                if (lastState is { } shown)
                {
                    Publish(shown with
                    {
                        Capture = Localization.Strings.Code(RecordingState.BlackoutNote, app.Title, until.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)),
                        SettingsPending = false
                    });
                }

                return;
            }
        }

        var closing = Detach();

        if (closing is null)
        {
            report($"{app.Title} завершился до старта записи");
            EndRun();
            return;
        }

        try
        {
            var result = closing.Stop();

            report(
                $"{app.Title} завершился, запись остановлена: кадров {result.FramesCaptured}, " +
                $"отброшено {result.FramesDropped}, клипов {Tally(closing)}");
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref failure, ex, null);
            report($"остановка записи не удалась: {ex.Message}");
        }
        finally
        {
            closing.Dispose();
        }

        EndRun();
        report($"жду возврата {app.Title} ещё {settings.Grace.TotalSeconds:F0} с");
    }

    private int Tally(ReplaySession closing)
    {
        clipsInRun += closing.Clips;
        return clipsInRun;
    }

    private void EndRun()
    {
        clipsInRun = 0;
        continuing = false;
    }

    private ReplaySession? Detach()
    {
        ReplaySession? current;

        lock (gate)
        {
            current = session;
            session = null;
        }

        afterApp = null;

        if (current is not null)
        {
            Publish(new RecordingState(false, null, null));
        }

        return current;
    }

    private void Meter()
    {
        ReplaySession? current;

        lock (gate)
        {
            current = session;
        }

        if (current is null)
        {
            return;
        }

        if (reportedMemory != 0 && Stopwatch.GetElapsedTime(reportedMemory) < MemoryInterval)
        {
            return;
        }

        reportedMemory = Stopwatch.GetTimestamp();

        try
        {
            memory(current.MemoryBytes);
        }
        catch (Exception ex)
        {
            trace($"счётчик памяти не отправился: {ex.Message}");
        }
    }

    private void DropSession()
    {
        var closing = Detach();

        if (closing is null)
        {
            return;
        }

        try
        {
            closing.Stop();
        }
        catch (Exception ex)
        {
            trace($"аварийная остановка: {ex.Message}");
        }
        finally
        {
            closing.Dispose();
        }

        targetApp = null;
        recovering = false;
        trouble = null;
        EndRun();
    }

    private void Close()
    {
        detector.Dispose();

        var closing = Detach();

        if (closing is null)
        {
            return;
        }

        try
        {
            var result = closing.Stop();
            report(
                $"запись остановлена: кадров {result.FramesCaptured}, " +
                $"отброшено {result.FramesDropped}, клипов {Tally(closing)}");
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref failure, ex, null);
            report($"остановка записи не удалась: {ex.Message}");
        }
        finally
        {
            closing.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        lock (gate)
        {
            stopping = true;
        }

        wake.Set();
        thread.Join();
        wake.Dispose();
    }
}
