using System.Diagnostics;

namespace Kebuz.Recorder.Buffering;

internal sealed record ClipMergeSettings(TimeSpan Delay, TimeSpan MaxLength);

internal sealed record ScheduledClip(int Index, int Marks, ClipWriteResult Result, TimeSpan Elapsed);

internal sealed class ClipScheduler : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly ReplayBuffer buffer;
    private readonly ClipMergeSettings settings;
    private readonly Func<int, DateTime, string> path;
    private readonly Action<ScheduledClip> saved;
    private readonly Action<string> trace;
    private readonly Lock gate = new();
    private readonly ManualResetEventSlim signal = new(false);
    private readonly Thread worker;

    private ClipHold? pending;
    private long deadline;
    private DateTime markedAt;
    private long limit;
    private int pendingMarks;
    private int marks;
    private int clips;
    private Exception? failure;
    private bool stopping;
    private bool disposed;

    public ClipScheduler(
        ReplayBuffer buffer,
        ClipMergeSettings settings,
        Func<int, DateTime, string> path,
        Action<ScheduledClip> saved,
        Action<string> trace)
    {
        this.buffer = buffer;
        this.settings = settings;
        this.path = path;
        this.saved = saved;
        this.trace = trace;

        worker = new Thread(Loop)
        {
            Name = "KebuzClipScheduler",
            IsBackground = true
        };

        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public int Marks => Volatile.Read(ref marks);

    public int Clips => Volatile.Read(ref clips);

    public Exception? Failure => Volatile.Read(ref failure);

    public void Trigger()
    {
        lock (gate)
        {
            if (stopping)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();

            if (pending is null)
            {
                pending = buffer.BeginClip();
                pendingMarks = 0;
                markedAt = DateTime.Now;
                limit = now + Stamps(settings.MaxLength);
                trace($"метка 1, клип сохраняется через {settings.Delay.TotalSeconds:F1} с тишины");
            }
            else
            {
                buffer.ExtendClip(pending);
                trace($"метка {pendingMarks + 1}, конец клипа отодвинут");
            }

            pendingMarks++;
            marks++;
            deadline = Math.Min(now + Stamps(settings.Delay), limit);
        }

        signal.Set();
    }

    private void Loop()
    {
        while (true)
        {
            signal.Reset();

            ClipHold? due = null;
            var count = 0;
            var at = DateTime.Now;
            var wait = Timeout.InfiniteTimeSpan;

            lock (gate)
            {
                if (pending is not null && (stopping || Stopwatch.GetTimestamp() >= deadline))
                {
                    due = pending;
                    count = pendingMarks;
                    at = markedAt;
                    pending = null;
                }
                else if (pending is not null)
                {
                    wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                }
                else if (stopping)
                {
                    return;
                }
            }

            if (due is not null)
            {
                Write(due, count, at);
                continue;
            }

            signal.Wait(wait);
        }
    }

    private void Write(ClipHold hold, int count, DateTime at)
    {
        using (hold)
        {
            var index = Clips + 1;
            var started = Stopwatch.GetTimestamp();

            try
            {
                var clip = buffer.Save(path(index, at), hold);
                var elapsed = Stopwatch.GetElapsedTime(started);

                if (clip is null)
                {
                    trace("буфер пуст, сохранять нечего");
                    return;
                }

                Volatile.Write(ref clips, index);
                saved(new ScheduledClip(index, count, clip, elapsed));
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                trace($"сохранение не удалось: {ex.Message}");
            }
        }
    }

    private static long Stamps(TimeSpan span)
    {
        return (long)(span.TotalSeconds * Stopwatch.Frequency);
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

        signal.Set();
        worker.Join(StopTimeout);
        signal.Dispose();
    }
}
