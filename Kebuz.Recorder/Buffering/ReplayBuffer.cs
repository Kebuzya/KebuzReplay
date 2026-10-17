using System.Runtime.InteropServices;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Ipc;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Buffering;

internal sealed record ReplayBufferSettings(
    TimeSpan SegmentLength,
    TimeSpan BufferLength,
    long MaxBytes,
    TimeSpan MergeMaxLength,
    AudioTrackSettings? Audio = null,
    uint? GameProcessId = null,
    Action<SourceHealth>? Health = null);

internal sealed class ReplayBuffer : IVideoEncoder
{
    private readonly AsyncVideoEncoder encoder;
    private readonly SegmentRing ring;
    private readonly AudioTrack? audio;

    private long floor = -1;
    private bool disposed;

    public ReplayBuffer(
        GraphicsDevice device,
        VideoEncoderSettings video,
        ReplayBufferSettings buffer,
        Action<string>? trace = null)
    {
        var frameDuration = TimeSpan.TicksPerSecond / video.FrameRate;
        var capacity = EstimateSegmentCapacity(video.PeakBitrateBps, buffer.SegmentLength);

        ring = new SegmentRing(
            new SegmentRingSettings(buffer.SegmentLength, buffer.BufferLength, buffer.MaxBytes, capacity),
            frameDuration);

        if (buffer.Audio is not null)
        {
            audio = AudioTrack.TryStart(
                buffer.Audio,
                buffer.GameProcessId,
                buffer.BufferLength,
                buffer.MergeMaxLength,
                trace ?? (_ => { }),
                buffer.Health);
        }

        encoder = new AsyncVideoEncoder(device, video, Store);
    }

    public string Name => encoder.Name;

    public EncoderControlReport Controls => encoder.Controls;

    public string InputFormat => encoder.InputFormat;

    public bool Scaling => encoder.Scaling;

    public SegmentRingStatus Status => ring.Status();

    public AudioTrack? Audio => audio;

    public void WriteFrame(ID3D11Texture2D frame, long timestampTicks)
    {
        encoder.WriteFrame(frame, timestampTicks);
    }

    public void Anchor(long originTicks)
    {
        audio?.Anchor(originTicks);
    }

    public void Finish()
    {
        encoder.Finish();
        audio?.Finish();
    }

    public ClipHold BeginClip()
    {
        return ring.Hold();
    }

    public void ExtendClip(ClipHold hold)
    {
        ring.Extend(hold);
    }

    public ClipWriteResult? Save(string path, ClipHold hold)
    {
        var encodedType = encoder.TryCloneEncodedType()
            ?? throw new InvalidOperationException("Тип потока ещё не известен, энкодер не отдал ни одного сэмпла");

        var audioType = audio?.TryCloneEncodedType();

        try
        {
            using var snapshot = ring.Snapshot(hold);

            if (snapshot is null)
            {
                return null;
            }

            snapshot.Audio = audio?.Take(snapshot.StartTicks, snapshot.LimitTicks);

            return ClipWriter.Write(path, encodedType, snapshot.Audio is null ? null : audioType, snapshot);
        }
        finally
        {
            Marshal.ReleaseComObject(encodedType);

            if (audioType is not null)
            {
                Marshal.ReleaseComObject(audioType);
            }
        }
    }

    public static long EstimateBufferBytes(uint peakBitrateBps, TimeSpan bufferLength)
    {
        return (long)(peakBitrateBps / 8d * bufferLength.TotalSeconds);
    }

    public static long EstimateAllocatedBytes(uint peakBitrateBps, TimeSpan bufferLength)
    {
        return EstimateBufferBytes(peakBitrateBps, bufferLength) * 5 / 4;
    }

    public static long EstimatePeakBytes(uint peakBitrateBps, TimeSpan bufferLength, TimeSpan mergeMaxLength)
    {
        return EstimateAllocatedBytes(peakBitrateBps, bufferLength + mergeMaxLength);
    }

    private static int EstimateSegmentCapacity(uint peakBitrateBps, TimeSpan segmentLength)
    {
        var estimate = EstimateAllocatedBytes(peakBitrateBps, segmentLength);
        return (int)Math.Clamp(estimate, 256 * 1024, 64 * 1024 * 1024);
    }

    private void Store(IMFSample sample)
    {
        ring.Append(sample);

        if (audio is null)
        {
            return;
        }

        var current = ring.FloorTicks();

        if (current == floor)
        {
            return;
        }

        floor = current;
        audio.Trim(current);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        encoder.Dispose();
        audio?.Dispose();
    }
}
