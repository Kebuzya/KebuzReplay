using System.Diagnostics;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;

namespace Kebuz.App.Supervising;

internal sealed class RecorderWatchdog : IDisposable
{
    private const int CrashLimit = 3;

    private static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly string executable;
    private readonly ChildPipe pipe;
    private readonly ChildJob job;
    private readonly Action<string> restarted;
    private readonly string[] extra;
    private readonly Queue<DateTime> crashes = new();
    private readonly Lock gate = new();

    private Process? current;
    private Thread? watching;
    private volatile bool stopping;
    private volatile bool ready;

    public RecorderWatchdog(
        string executable,
        ChildPipe pipe,
        ChildJob job,
        Action<string> restarted,
        string[]? extra = null)
    {
        this.executable = executable;
        this.pipe = pipe;
        this.job = job;
        this.restarted = restarted;
        this.extra = extra ?? [];
    }

    public void NoteReady()
    {
        ready = true;
    }

    public void Start()
    {
        if (!Launch(false))
        {
            return;
        }

        watching = new Thread(Watch)
        {
            Name = "KebuzWatchdog",
            IsBackground = true
        };

        watching.Start();
    }

    public void Stop()
    {
        stopping = true;

        Process? process;

        lock (gate)
        {
            process = current;
        }

        if (process is null || process.HasExited)
        {
            Log.Report("Recorder уже завершён");
            return;
        }

        Log.Report("Отправляю Recorder Shutdown");

        if (pipe.Send(new PipeMessage(PipeMessages.Shutdown))
            && process.WaitForExit((int)StopTimeout.TotalMilliseconds))
        {
            Log.Report($"Recorder завершился штатно, код {process.ExitCode}");
            return;
        }

        Log.Error($"Recorder не завершился за {StopTimeout.TotalSeconds:F0} с, убиваю pid {process.Id}");

        try
        {
            process.Kill(true);
            process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log.Error("не удалось убить Recorder", ex);
        }
    }

    public void Dispose()
    {
        stopping = true;

        lock (gate)
        {
            current?.Dispose();
            current = null;
        }
    }

    private void Watch()
    {
        while (!stopping)
        {
            Process? process;

            lock (gate)
            {
                process = current;
            }

            if (process is null)
            {
                return;
            }

            process.WaitForExit();

            if (stopping)
            {
                return;
            }

            if (!ready && process.ExitCode == 0)
            {
                Log.Error(
                    $"Recorder вышел с кодом 0, не сообщив о готовности, pid {process.Id}: " +
                    "он отказался стартовать, перезапусков не будет");
                return;
            }

            Log.Error($"Recorder завершился неожиданно, код {process.ExitCode}, pid {process.Id}");

            if (Exhausted())
            {
                Log.Error(
                    $"Recorder падал {CrashLimit} раза за {CrashWindow.TotalSeconds:F0} с, " +
                    "перезапуски прекращены, супервизор остаётся жив");
                return;
            }

            var reason = Kebuz.Recorder.Localization.Strings.Code("Restart.ExitCode", process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Thread.Sleep(RestartDelay);

            if (stopping || !Launch(true))
            {
                return;
            }

            restarted(reason);
        }
    }

    private bool Exhausted()
    {
        var now = DateTime.UtcNow;

        crashes.Enqueue(now);

        while (crashes.Count > 0 && now - crashes.Peek() > CrashWindow)
        {
            crashes.Dequeue();
        }

        return crashes.Count >= CrashLimit;
    }

    private bool Launch(bool restart)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        info.ArgumentList.Add("--recorder");
        info.ArgumentList.Add("--watch");
        info.ArgumentList.Add("--supervised");

        foreach (var argument in extra)
        {
            info.ArgumentList.Add(argument);
        }

        Process? started;

        ready = false;

        try
        {
            started = Process.Start(info);
        }
        catch (Exception ex)
        {
            Log.Error("не удалось запустить Recorder", ex);
            return false;
        }

        if (started is null)
        {
            Log.Error("не удалось запустить Recorder");
            return false;
        }

        job.Assign(started);

        Process? previous;

        lock (gate)
        {
            previous = current;
            current = started;
        }

        previous?.Dispose();

        Log.Report($"Recorder {(restart ? "перезапущен" : "запущен")}, pid {started.Id}");
        return true;
    }
}
