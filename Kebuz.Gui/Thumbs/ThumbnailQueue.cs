using System.Windows.Media;
using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Gui.Thumbs;

internal sealed class ThumbnailQueue : IDisposable
{
    private readonly Queue<Request> waiting = new();
    private readonly AutoResetEvent pulse = new(false);
    private readonly object gate = new();

    private List<string>? sweep;
    private Thread? worker;
    private int token;
    private bool working;
    private bool stopping;

    public event Action<int, string, ImageSource>? Ready;

    public event Action<int>? Settled;

    public void Cancel(int next)
    {
        lock (gate)
        {
            token = next;
            waiting.Clear();
            sweep = null;
        }
    }

    public void Sweep(int at, IReadOnlyCollection<string> alive)
    {
        lock (gate)
        {
            if (at != token || stopping)
            {
                return;
            }

            sweep = [.. alive];
        }

        Wake();
    }

    public void Want(int at, string path, double seconds)
    {
        lock (gate)
        {
            if (at != token || stopping)
            {
                return;
            }

            waiting.Enqueue(new Request(at, path, seconds));
        }

        Wake();
    }

    public void Dispose()
    {
        Thread? running;

        lock (gate)
        {
            stopping = true;
            waiting.Clear();
            sweep = null;
            running = worker;
        }

        if (running is null)
        {
            return;
        }

        pulse.Set();
        running.Join(TimeSpan.FromSeconds(2));
    }

    private void Wake()
    {
        lock (gate)
        {
            if (stopping)
            {
                return;
            }

            if (worker is null)
            {
                var fresh = new Thread(Loop) { IsBackground = true, Name = "KebuzThumbs" };
                fresh.SetApartmentState(ApartmentState.MTA);
                worker = fresh;
                fresh.Start();
            }
        }

        pulse.Set();
    }

    private void Loop()
    {
        while (true)
        {
            List<string>? clean;
            Request? next = null;
            int at;

            lock (gate)
            {
                if (stopping)
                {
                    return;
                }

                at = token;
                clean = sweep;
                sweep = null;

                if (clean is null && waiting.Count > 0)
                {
                    next = waiting.Dequeue();
                }
            }

            if (clean is not null)
            {
                working = true;
                ThumbnailCache.Sweep(clean);
                continue;
            }

            if (next is not null)
            {
                working = true;
                Work(next);
                continue;
            }

            if (working)
            {
                working = false;
                Settled?.Invoke(at);
                continue;
            }

            pulse.WaitOne();
        }
    }

    private void Work(Request request)
    {
        try
        {
            if (ThumbnailCache.Take(request.Path, request.Seconds) is not { } picture)
            {
                return;
            }

            lock (gate)
            {
                if (request.Token != token || stopping)
                {
                    return;
                }
            }

            Ready?.Invoke(request.Token, request.Path, picture);
        }
        catch (Exception ex)
        {
            Log.Error($"миниатюра не снялась: {request.Path}", ex);
        }
    }

    private sealed record Request(int Token, string Path, double Seconds);
}
