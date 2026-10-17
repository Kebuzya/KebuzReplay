namespace Kebuz.Recorder.Audio;

internal readonly record struct AudioFrameEntry(long TimestampTicks, long DurationTicks, int Length)
{
    public long EndTicks => TimestampTicks + DurationTicks;
}

internal delegate void AudioFrameVisitor(AudioFrameEntry entry, ReadOnlySpan<byte> payload);

internal sealed record AudioTake(byte[] Data, IReadOnlyList<AudioFrameEntry> Frames)
{
    public long StartTicks => Frames.Count is 0 ? 0 : Frames[0].TimestampTicks;

    public long EndTicks => Frames.Count is 0 ? 0 : Frames[^1].EndTicks;
}

internal sealed record AudioRingStatus(
    int Frames,
    long Bytes,
    long Capacity,
    TimeSpan Duration,
    long Trimmed,
    long Overrun);

internal sealed class AudioRing
{
    private readonly Lock gate = new();
    private readonly byte[] data;
    private readonly Queue<AudioFrameEntry> frames = new();

    private byte[] scratch = [];
    private long head;
    private long tail;

    public AudioRing(int capacityBytes)
    {
        data = new byte[Math.Max(capacityBytes, 1)];
    }

    public long Trimmed { get; private set; }

    public long Overrun { get; private set; }

    public static int EstimateBytes(uint bitrateBps, TimeSpan bufferLength, TimeSpan mergeMaxLength)
    {
        var seconds = bufferLength.TotalSeconds + mergeMaxLength.TotalSeconds;
        return (int)(bitrateBps / 8d * seconds * 1.25d);
    }

    public void Append(ReadOnlySpan<byte> payload, long timestampTicks, long durationTicks)
    {
        lock (gate)
        {
            if (payload.Length > data.Length)
            {
                Overrun++;
                return;
            }

            while (data.Length - (tail - head) < payload.Length)
            {
                Drop();
                Overrun++;
            }

            Copy(payload);
            frames.Enqueue(new AudioFrameEntry(timestampTicks, durationTicks, payload.Length));
            tail += payload.Length;
        }
    }

    public void Trim(long floorTicks)
    {
        lock (gate)
        {
            while (frames.Count > 0 && frames.Peek().EndTicks <= floorTicks)
            {
                Drop();
                Trimmed++;
            }
        }
    }

    public AudioRingStatus Status()
    {
        lock (gate)
        {
            return new AudioRingStatus(
                frames.Count,
                tail - head,
                data.Length,
                TimeSpan.FromTicks(SpanTicks()),
                Trimmed,
                Overrun);
        }
    }

    public AudioTake? Take(long fromTicks, long limitTicks)
    {
        lock (gate)
        {
            var taken = new List<AudioFrameEntry>();
            var position = head;
            var start = 0L;
            var bytes = 0;

            foreach (var entry in frames)
            {
                if (entry.TimestampTicks >= fromTicks && entry.TimestampTicks < limitTicks)
                {
                    if (taken.Count is 0)
                    {
                        start = position;
                    }

                    taken.Add(entry);
                    bytes += entry.Length;
                }

                position += entry.Length;
            }

            if (taken.Count is 0)
            {
                return null;
            }

            var payload = new byte[bytes];
            Slice(start, bytes).CopyTo(payload);

            return new AudioTake(payload, taken);
        }
    }

    public void ForEach(AudioFrameVisitor visit)
    {
        lock (gate)
        {
            var position = head;

            foreach (var entry in frames)
            {
                visit(entry, Slice(position, entry.Length));
                position += entry.Length;
            }
        }
    }

    private long SpanTicks()
    {
        if (frames.Count is 0)
        {
            return 0;
        }

        var first = frames.Peek();
        var last = first;

        foreach (var entry in frames)
        {
            last = entry;
        }

        return last.EndTicks - first.TimestampTicks;
    }

    private ReadOnlySpan<byte> Slice(long position, int length)
    {
        var index = (int)(position % data.Length);
        var first = Math.Min(length, data.Length - index);

        if (first == length)
        {
            return data.AsSpan(index, length);
        }

        if (scratch.Length < length)
        {
            scratch = new byte[length];
        }

        data.AsSpan(index, first).CopyTo(scratch);
        data.AsSpan(0, length - first).CopyTo(scratch.AsSpan(first));
        return scratch.AsSpan(0, length);
    }

    private void Copy(ReadOnlySpan<byte> payload)
    {
        var index = (int)(tail % data.Length);
        var first = Math.Min(payload.Length, data.Length - index);

        payload[..first].CopyTo(data.AsSpan(index));

        if (first < payload.Length)
        {
            payload[first..].CopyTo(data.AsSpan(0));
        }
    }

    private void Drop()
    {
        if (frames.Count is 0)
        {
            return;
        }

        head += frames.Dequeue().Length;
    }
}
