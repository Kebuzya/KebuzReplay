using System.Runtime.InteropServices;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Encoding;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Buffering;

internal readonly record struct SegmentSample(
    long TimestampTicks,
    long DurationTicks,
    int Offset,
    int Length,
    bool KeyFrame);

internal sealed class BufferSegment
{
    public BufferSegment(byte[] data, bool tracked, long sequence)
    {
        Data = data;
        Tracked = tracked;
        Sequence = sequence;
    }

    public byte[] Data { get; set; }

    public bool Tracked { get; }

    public long Sequence { get; }

    public int Used { get; set; }

    public int References { get; set; } = 1;

    public List<SegmentSample> Samples { get; } = [];

    public long StartTicks => Samples.Count is 0 ? 0 : Samples[0].TimestampTicks;

    public long EndTicks => Samples.Count is 0
        ? 0
        : Samples[^1].TimestampTicks + Samples[^1].DurationTicks;

    public long DurationTicks => EndTicks - StartTicks;

    public BufferSegment Detach()
    {
        var copy = new BufferSegment(Data.AsSpan(0, Used).ToArray(), false, Sequence)
        {
            Used = Used
        };

        copy.Samples.AddRange(Samples);
        return copy;
    }
}

internal sealed record SegmentRingSettings(
    TimeSpan SegmentLength,
    TimeSpan BufferLength,
    long MaxBytes,
    int SegmentCapacity);

internal sealed record SegmentRingStatus(
    int Segments,
    TimeSpan Duration,
    long Bytes,
    long PayloadBytes,
    int Samples,
    int Skipped);

internal sealed class SegmentRing
{
    private const int SpareLimit = 2;

    private readonly SegmentRingSettings settings;
    private readonly long defaultDurationTicks;
    private readonly long closeThresholdTicks;
    private readonly Lock gate = new();
    private readonly Queue<BufferSegment> closed = new();
    private readonly Stack<byte[]> spare = new();
    private readonly List<ClipHold> holds = [];

    private BufferSegment? open;
    private long bytes;
    private long endTicks;
    private long nextSequence;
    private int skipped;

    public SegmentRing(SegmentRingSettings settings, long defaultDurationTicks)
    {
        this.settings = settings;
        this.defaultDurationTicks = defaultDurationTicks;
        closeThresholdTicks = settings.SegmentLength.Ticks - (defaultDurationTicks / 2);
    }

    public unsafe void Append(IMFSample sample)
    {
        sample.GetSampleTime(out var time);

        long duration;
        try
        {
            sample.GetSampleDuration(out duration);
        }
        catch (COMException)
        {
            duration = defaultDurationTicks;
        }

        if (duration <= 0)
        {
            duration = defaultDurationTicks;
        }

        var keyFrame = ((IMFAttributes)sample).TryGetUInt32(PInvoke.MFSampleExtension_CleanPoint) is 1;

        sample.ConvertToContiguousBuffer(out var buffer);

        try
        {
            buffer.Lock(out var pointer, out _, out var length);

            try
            {
                Store(new ReadOnlySpan<byte>(pointer, (int)length), time, duration, keyFrame);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    public SegmentRingStatus Status()
    {
        lock (gate)
        {
            var count = closed.Count;
            var payload = 0L;
            var samples = 0;

            foreach (var segment in closed)
            {
                payload += segment.Used;
                samples += segment.Samples.Count;
            }

            if (open is not null)
            {
                count++;
                payload += open.Used;
                samples += open.Samples.Count;
            }

            return new SegmentRingStatus(count, TimeSpan.FromTicks(SpanTicks()), bytes, payload, samples, skipped);
        }
    }

    public long FloorTicks()
    {
        lock (gate)
        {
            return closed.Count > 0 ? closed.Peek().StartTicks : open?.StartTicks ?? 0;
        }
    }

    public ClipHold Hold()
    {
        lock (gate)
        {
            var first = closed.Count > 0
                ? closed.Peek().Sequence
                : open?.Sequence ?? nextSequence;

            var hold = new ClipHold(this, first, endTicks);
            holds.Add(hold);
            return hold;
        }
    }

    public void Extend(ClipHold hold)
    {
        lock (gate)
        {
            hold.EndTicks = endTicks;
        }
    }

    internal void ReleaseHold(ClipHold hold)
    {
        lock (gate)
        {
            holds.Remove(hold);
        }
    }

    public ClipSnapshot? Snapshot(ClipHold hold)
    {
        lock (gate)
        {
            var parts = new List<BufferSegment>(closed.Count + 1);

            foreach (var segment in closed)
            {
                if (segment.Sequence < hold.Sequence)
                {
                    continue;
                }

                if (segment.StartTicks >= hold.EndTicks)
                {
                    break;
                }

                segment.References++;
                parts.Add(segment);
            }

            if (open is { Samples.Count: > 0 }
                && open.Sequence >= hold.Sequence
                && open.StartTicks < hold.EndTicks)
            {
                parts.Add(open.Detach());
            }

            return parts.Count is 0 ? null : new ClipSnapshot(this, parts, hold.EndTicks);
        }
    }

    internal void ReleaseSnapshot(IReadOnlyList<BufferSegment> parts)
    {
        lock (gate)
        {
            foreach (var segment in parts)
            {
                Release(segment);
            }
        }
    }

    private void Store(ReadOnlySpan<byte> payload, long time, long duration, bool keyFrame)
    {
        lock (gate)
        {
            if (open is null)
            {
                if (!keyFrame)
                {
                    skipped++;
                    return;
                }

                open = Rent();
            }
            else if (keyFrame && open.DurationTicks >= closeThresholdTicks)
            {
                closed.Enqueue(open);
                open = Rent();
            }

            Write(open, payload, time, duration, keyFrame);
            Trim();
        }
    }

    private void Write(BufferSegment segment, ReadOnlySpan<byte> payload, long time, long duration, bool keyFrame)
    {
        var required = segment.Used + payload.Length;

        if (required > segment.Data.Length)
        {
            var step = Math.Max(settings.SegmentCapacity / 8, 64 * 1024);
            var grown = new byte[Math.Max(required, segment.Data.Length + step)];
            Array.Copy(segment.Data, grown, segment.Used);
            bytes += grown.Length - segment.Data.Length;
            segment.Data = grown;
        }

        payload.CopyTo(segment.Data.AsSpan(segment.Used));
        segment.Samples.Add(new SegmentSample(time, duration, segment.Used, payload.Length, keyFrame));
        segment.Used = required;
        endTicks = time + duration;
    }

    private void Trim()
    {
        while (bytes > settings.MaxBytes && spare.Count > 0)
        {
            bytes -= spare.Pop().Length;
        }

        while (closed.Count > 0)
        {
            var oldest = closed.Peek();

            if (oldest.References > 1 || Held(oldest))
            {
                return;
            }

            if (bytes <= settings.MaxBytes && SpanTicks() - oldest.DurationTicks < settings.BufferLength.Ticks)
            {
                return;
            }

            Release(closed.Dequeue());
        }
    }

    private bool Held(BufferSegment segment)
    {
        foreach (var hold in holds)
        {
            if (segment.Sequence >= hold.Sequence)
            {
                return true;
            }
        }

        return false;
    }

    private long SpanTicks()
    {
        if (open is null)
        {
            return 0;
        }

        return open.EndTicks - (closed.Count > 0 ? closed.Peek().StartTicks : open.StartTicks);
    }

    private BufferSegment Rent()
    {
        var sequence = nextSequence++;

        if (spare.Count > 0)
        {
            return new BufferSegment(spare.Pop(), true, sequence);
        }

        var data = new byte[settings.SegmentCapacity];
        bytes += data.Length;
        return new BufferSegment(data, true, sequence);
    }

    private void Release(BufferSegment segment)
    {
        segment.References--;

        if (segment.References > 0 || !segment.Tracked)
        {
            return;
        }

        if (spare.Count < SpareLimit
            && segment.Data.Length >= settings.SegmentCapacity
            && bytes <= settings.MaxBytes)
        {
            spare.Push(segment.Data);
        }
        else
        {
            bytes -= segment.Data.Length;
        }

        segment.Data = [];
        segment.Used = 0;
        segment.Samples.Clear();
    }
}

internal sealed class ClipHold : IDisposable
{
    private readonly SegmentRing ring;

    private bool disposed;

    internal ClipHold(SegmentRing ring, long sequence, long endTicks)
    {
        this.ring = ring;
        Sequence = sequence;
        EndTicks = endTicks;
    }

    internal long Sequence { get; }

    internal long EndTicks { get; set; }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ring.ReleaseHold(this);
    }
}

internal sealed class ClipSnapshot : IDisposable
{
    private readonly SegmentRing ring;

    private bool disposed;

    internal ClipSnapshot(SegmentRing ring, IReadOnlyList<BufferSegment> segments, long limitTicks)
    {
        this.ring = ring;
        Segments = segments;
        LimitTicks = limitTicks;
    }

    internal IReadOnlyList<BufferSegment> Segments { get; }

    internal long LimitTicks { get; }

    internal AudioTake? Audio { get; set; }

    public long StartTicks => Segments[0].StartTicks;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ring.ReleaseSnapshot(Segments);
    }
}
