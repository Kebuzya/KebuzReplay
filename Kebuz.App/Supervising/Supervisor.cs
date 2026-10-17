using System.Diagnostics;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Settings;

namespace Kebuz.App.Supervising;

internal static class Supervisor
{
    private static readonly ManualResetEventSlim finish = new(false);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);
    private static readonly Lock gate = new();
    private static readonly TimeSpan GuiExitGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GuiRestartWindow = TimeSpan.FromMinutes(5);
    private const int GuiRestartLimit = 3;
    private static readonly List<long> guiFailures = [];

    private static RecorderWatchdog? recorder;
    private static ChildPipe? toRecorder;
    private static ChildPipe? toGui;
    private static KebuzSettings settings = KebuzSettings.Default;
    private static PipeMessage? lastState;
    private static PipeMessage? lastHotkey;
    private static PipeMessage? lastShot;
    private static bool levelsWanted;
    private static Process? guiProcess;
    private static string? self;
    private static ChildJob? children;

    public static int Run(string[] args)
    {
        using var mutex = new Mutex(true, PipeNames.SupervisorMutex, out var owned);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, PipeNames.ShowEvent);
        using var leave = new EventWaitHandle(false, EventResetMode.ManualReset, PipeNames.ExitEvent);

        if (!owned)
        {
            Windows.Win32.PInvoke.AllowSetForegroundWindow(Windows.Win32.PInvoke.ASFW_ANY);
            show.Set();
            Console.WriteLine("KebuzReplay уже запущен, открываю его окно");
            return 0;
        }

        Log.Configure(ParentConsole.Attached, false, "kebuz-app-log.txt");

        if (Kebuz.Recorder.RecorderHost.Reject(args) is { } rejected)
        {
            Log.Error($"супервизор не поднялся: {rejected}");
            Log.Flush();
            return 2;
        }

        Adopt();

        var executable = Environment.ProcessPath;

        if (executable is null)
        {
            Log.Error("не удалось определить собственный путь, супервизор не поднялся");
            Log.Flush();
            return 1;
        }

        Log.Report($"Супервизор запущен, pid {Environment.ProcessId}, {executable}");

        Console.CancelKeyPress += OnCancelKey;

        settings = LoadSettings();

        using var job = ChildJob.Create();
        using var pipe = new ChildPipe(PipeNames.RecorderPipe, "Recorder", FromRecorder, PushToRecorder);
        using var guiPipe = new ChildPipe(PipeNames.GuiPipe, "GUI", FromGui, PushToGui);
        using var watchdog = new RecorderWatchdog(executable, pipe, job, NoteRestart, args);

        if (args.Length > 0)
        {
            Log.Report($"Recorder получит дополнительные аргументы: {string.Join(" ", args)}");
        }

        toRecorder = pipe;
        toGui = guiPipe;
        recorder = watchdog;
        watchdog.Start();

        self = executable;
        children = job;

        var gui = StartGui(executable, job, false);

        if (gui is null)
        {
            watchdog.Stop();
            Log.Report("Супервизор завершается, GUI не поднялся");
            Log.Flush();
            return 1;
        }

        lock (gate)
        {
            guiProcess = gui;
        }

        Watch(gui);
        WatchShow(show, leave);

        finish.Wait();

        watchdog.Stop();

        lock (gate)
        {
            gui = guiProcess!;
        }

        CloseGui(gui);

        Log.Report("Супервизор завершается");
        Log.Flush();

        return 0;
    }

    public static int RequestExit()
    {
        if (!EventWaitHandle.TryOpenExisting(PipeNames.ExitEvent, out var leave))
        {
            Console.WriteLine("KebuzReplay не запущен");
            return 0;
        }

        using (leave)
        {
            leave.Set();
        }

        using var mutex = new Mutex(false, PipeNames.SupervisorMutex);

        try
        {
            if (!mutex.WaitOne(ExitTimeout))
            {
                Console.WriteLine($"KebuzReplay не завершился за {ExitTimeout.TotalSeconds:F0} с");
                return 1;
            }
        }
        catch (AbandonedMutexException)
        {
        }

        mutex.ReleaseMutex();
        Console.WriteLine("KebuzReplay завершён");
        return 0;
    }

    private static void WatchShow(EventWaitHandle show, EventWaitHandle leave)
    {
        var thread = new Thread(() =>
        {
            var handles = new[] { show, leave, finish.WaitHandle };

            while (true)
            {
                var signal = WaitHandle.WaitAny(handles);

                if (signal == 0)
                {
                    Log.Report("повторный запуск, прошу GUI показать окно");
                    toGui?.Send(new PipeMessage(PipeMessages.ShowWindow));
                    continue;
                }

                if (signal == 1)
                {
                    Log.Report("попросили завершиться снаружи (--exit), завершаю работу");
                    finish.Set();
                }

                return;
            }
        })
        {
            Name = "KebuzShowRequests",
            IsBackground = true
        };

        thread.Start();
    }

    private static void Adopt()
    {
        try
        {
            var adopted = AppPaths.AdoptOldFiles();

            if (adopted.Count > 0)
            {
                Log.Report($"Настройки перенесены из {AppContext.BaseDirectory} в {AppPaths.Settings}: {string.Join(", ", adopted)}");
            }

            Log.Report(AppPaths.Portable
                ? $"Переносной режим, данные в {AppPaths.Settings}"
                : $"Настройки в {AppPaths.Settings}, логи и кэш в {AppPaths.Local}");
        }
        catch (Exception ex)
        {
            Log.Error("старые настройки рядом с exe не перенеслись", ex);
        }
    }

    private static KebuzSettings LoadSettings()
    {
        try
        {
            var loaded = SettingsStore.Load();
            Log.Report($"Настройки: {SettingsStore.DefaultPath}, {loaded}");
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось прочитать {SettingsStore.DefaultPath}, иду на дефолтах", ex);
            return KebuzSettings.Default;
        }
    }

    private static KebuzSettings Current()
    {
        lock (gate)
        {
            return settings;
        }
    }

    private static void PushToRecorder()
    {
        if (toRecorder?.Send(PipeMessages.UpdateSettings, Current()) is true)
        {
            Log.Trace("настройки отправлены Recorder");
        }

        bool wanted;

        lock (gate)
        {
            wanted = levelsWanted;
        }

        if (wanted)
        {
            toRecorder?.Send(PipeMessages.WatchAudioLevels, new LevelWatch(true));
        }
    }

    private static void PushToGui()
    {
        if (toGui?.Send(PipeMessages.UpdateSettings, Current()) is true)
        {
            Log.Trace("настройки отправлены GUI");
        }

        PipeMessage? state;
        PipeMessage? hotkey;
        PipeMessage? shot;

        lock (gate)
        {
            state = lastState;
            hotkey = lastHotkey;
            shot = lastShot;
        }

        if (state is not null)
        {
            toGui?.Send(state);
        }

        if (shot is not null)
        {
            toGui?.Send(shot);
        }

        if (hotkey is not null)
        {
            toGui?.Send(hotkey);
        }
    }

    private static void NoteRestart(string reason)
    {
        toGui?.Send(PipeMessages.RecorderRestarted, new RestartInfo(reason));
    }

    private static void Accept(KebuzSettings next)
    {
        lock (gate)
        {
            settings = next;
        }

        try
        {
            SettingsStore.Save(next);
            Log.Report($"Настройки изменены из GUI: {next}");
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось записать {SettingsStore.DefaultPath}", ex);
        }

        toRecorder?.Send(PipeMessages.UpdateSettings, next);
    }

    private static void OnCancelKey(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        finish.Set();
    }

    private static void FromRecorder(PipeMessage message)
    {
        switch (message.Type)
        {
            case PipeMessages.Ready:
                recorder?.NoteReady();
                Log.Report("Recorder подключился по трубе и готов");
                return;

            case PipeMessages.RecordingStateChanged:
                lock (gate)
                {
                    lastState = message;

                    if (PipeProtocol.Unpack<RecordingState>(message) is { Recording: false })
                    {
                        lastShot = null;
                    }
                }

                toGui?.Forward(message);
                return;

            case PipeMessages.CaptureShown:
                lock (gate)
                {
                    lastShot = message;
                }

                toGui?.Forward(message);
                return;

            case PipeMessages.HotkeyChanged:
                lock (gate)
                {
                    lastHotkey = message;
                }

                if (PipeProtocol.Unpack<HotkeyState>(message) is { Registered: false } refused)
                {
                    Log.Error($"хоткей {refused.Combination} не зарегистрирован: {refused.Complaint}");
                }

                toGui?.Forward(message);
                return;

            case PipeMessages.ClipSaved:
            case PipeMessages.BufferMemoryUsage:
            case PipeMessages.AudioSourceChanged:
            case PipeMessages.AudioLevels:
            case PipeMessages.GameDetected:
                toGui?.Forward(message);
                return;

            case PipeMessages.CaptureRecovered:
                Log.Report($"Recorder восстановил захват: {PipeProtocol.Unpack<RecoveryInfo>(message)?.Reason}");
                toGui?.Forward(message);
                return;
        }

        Log.Trace($"от Recorder получено необрабатываемое сообщение {message.Type}");
    }

    private static void FromGui(PipeMessage message)
    {
        switch (message.Type)
        {
            case PipeMessages.Shutdown:
                Log.Report("GUI попросил завершить работу");
                finish.Set();
                return;

            case PipeMessages.UpdateSettings:
                if (PipeProtocol.Unpack<KebuzSettings>(message) is { } next)
                {
                    Accept(next);
                    return;
                }

                Log.Error("настройки от GUI не разобрались, файл не тронут");
                return;

            case PipeMessages.MarkClip:
                Log.Trace("метка из GUI, передаю Recorder");
                toRecorder?.Send(new PipeMessage(PipeMessages.MarkClip));
                return;

            case PipeMessages.ApplySettingsNow:
                Log.Report("GUI просит применить настройки сейчас, передаю Recorder");
                toRecorder?.Send(new PipeMessage(PipeMessages.ApplySettingsNow));
                return;

            case PipeMessages.WatchAudioLevels:
                WatchLevels(PipeProtocol.Unpack<LevelWatch>(message)?.On ?? false);
                return;
        }

        Log.Trace($"от GUI получено необрабатываемое сообщение {message.Type}");
    }

    private static void WatchLevels(bool on)
    {
        lock (gate)
        {
            levelsWanted = on;
        }

        toRecorder?.Send(PipeMessages.WatchAudioLevels, new LevelWatch(on));
    }

    private static Process? StartGui(string executable, ChildJob job, bool tray)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        info.ArgumentList.Add("--gui");

        if (tray)
        {
            info.ArgumentList.Add("--tray");
        }

        try
        {
            var started = Process.Start(info);

            if (started is null)
            {
                Log.Error("не удалось запустить GUI");
                return null;
            }

            job.Assign(started);

            Log.Report($"GUI запущен, pid {started.Id}");
            return started;
        }
        catch (Exception ex)
        {
            Log.Error("не удалось запустить GUI", ex);
            return null;
        }
    }

    private static void Watch(Process gui)
    {
        gui.EnableRaisingEvents = true;

        gui.Exited += (_, _) => Lost(gui);
    }

    private static void Lost(Process gui)
    {
        if (finish.Wait(GuiExitGrace))
        {
            return;
        }

        WatchLevels(false);

        var now = Stopwatch.GetTimestamp();
        guiFailures.RemoveAll(stamp => Stopwatch.GetElapsedTime(stamp, now) > GuiRestartWindow);

        if (guiFailures.Count >= GuiRestartLimit)
        {
            Log.Error(
                $"GUI завершился сам, код {gui.ExitCode}, {guiFailures.Count + 1}-й раз за {GuiRestartWindow.TotalMinutes:F0} мин, " +
                "больше не поднимаю; запись продолжается, окна и трея не будет до следующего запуска");
            return;
        }

        guiFailures.Add(now);
        Log.Error($"GUI завершился сам, код {gui.ExitCode}, поднимаю его заново в трее");

        var next = StartGui(self!, children!, true);

        if (next is null)
        {
            return;
        }

        lock (gate)
        {
            guiProcess = next;
        }

        Watch(next);

        if (finish.IsSet)
        {
            CloseGui(next);
        }
    }

    private static void CloseGui(Process gui)
    {
        if (gui.HasExited)
        {
            Log.Report($"GUI уже завершён, код {gui.ExitCode}");
            return;
        }

        Log.Report($"Закрываю GUI, pid {gui.Id}");

        try
        {
            gui.Kill(true);
            gui.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log.Error("не удалось закрыть GUI", ex);
        }
    }
}
