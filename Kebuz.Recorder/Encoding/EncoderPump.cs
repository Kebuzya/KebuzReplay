using System.Collections.Concurrent;
using System.Diagnostics;
using Kebuz.Recorder.Capture;
using Windows.Win32.Graphics.Direct3D11;

namespace Kebuz.Recorder.Encoding;

internal readonly record struct PendingFrame(ID3D11Texture2D Texture, long TimestampTicks);

internal sealed class EncoderPump : IDisposable
{
    private readonly IVideoEncoder encoder;
    private readonly TexturePool pool;
    private readonly BlockingCollection<PendingFrame> queue;
    private readonly Thread thread;

    private long lastProgress;
    private int written;
    private int dropped;
    private Exception? failure;
    private bool disposed;

    public EncoderPump(IVideoEncoder encoder, TexturePool pool, int capacity)
    {
        this.encoder = encoder;
        this.pool = pool;
        queue = new BlockingCollection<PendingFrame>(capacity);
        lastProgress = Stopwatch.GetTimestamp();

        thread = new Thread(Loop)
        {
            Name = "KebuzEncoder",
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public int Written => Volatile.Read(ref written);

    public int Dropped => Volatile.Read(ref dropped);

    public Exception? Failure => Volatile.Read(ref failure);

    public void Submit(PendingFrame frame)
    {
        if (!queue.IsAddingCompleted && Failure is null && queue.TryAdd(frame))
        {
            return;
        }

        Interlocked.Increment(ref dropped);
        pool.Return(frame.Texture);
    }

    public bool TryComplete(TimeSpan stallTimeout)
    {
        queue.CompleteAdding();

        var start = Stopwatch.GetTimestamp();

        while (!thread.Join(200))
        {
            var reference = Math.Max(start, Volatile.Read(ref lastProgress));
            if (Stopwatch.GetElapsedTime(reference) > stallTimeout)
            {
                return false;
            }
        }

        return true;
    }

    private void Loop()
    {
        foreach (var frame in queue.GetConsumingEnumerable())
        {
            if (Failure is not null)
            {
                pool.Return(frame.Texture);
                continue;
            }

            try
            {
                encoder.WriteFrame(frame.Texture, frame.TimestampTicks);
                Interlocked.Increment(ref written);
                Volatile.Write(ref lastProgress, Stopwatch.GetTimestamp());
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure, ex);
            }
            finally
            {
                pool.Return(frame.Texture);
            }
        }

        if (Failure is not null)
        {
            return;
        }

        try
        {
            encoder.Finish();
            Volatile.Write(ref lastProgress, Stopwatch.GetTimestamp());
        }
        catch (Exception ex)
        {
            Volatile.Write(ref failure, ex);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (!queue.IsAddingCompleted)
        {
            queue.CompleteAdding();
        }

        queue.Dispose();
    }
}
