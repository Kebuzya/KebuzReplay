using System.Runtime.InteropServices;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Ipc;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal sealed class ContinuousEncoder : IVideoEncoder
{
    private const long QueueLimit = 32 * 1024 * 1024;
    private sealed record Packet(byte[] Data, long Time, long Duration, bool Key);
    private readonly AsyncVideoEncoder video;
    private readonly AudioTrack? audio;
    private readonly Queue<Packet> videoPackets = new();
    private readonly Queue<Packet> audioPackets = new();
    private readonly Lock gate = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly Thread writerThread;
    private readonly string path;
    private long queuedBytes;
    private long end;
    private Exception? failure;
    private bool finishing;
    private bool finished;
    private bool disposed;

    public ContinuousEncoder(GraphicsDevice device, VideoEncoderSettings settings, AudioTrackSettings? sound,
        uint? pid, string path, Action<string> trace, Action<SourceHealth> health)
    {
        this.path = path;
        if (sound is not null)
            audio = AudioTrack.TryStart(sound, pid, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), trace, health);
        try
        {
            video = new AsyncVideoEncoder(device, settings, StoreVideo);
            if (audio is not null)
                audio.Encoded = (data, time, duration) => Enqueue(true, new Packet(data.ToArray(), time, duration, true));
            writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "KebuzContinuousWriter" };
            writerThread.SetApartmentState(ApartmentState.MTA);
            writerThread.Start();
        }
        catch
        {
            audio?.Dispose();
            throw;
        }
    }

    public Exception? Failure => Volatile.Read(ref failure);
    public double Seconds => Volatile.Read(ref end) / (double)TimeSpan.TicksPerSecond;
    public long Bytes => File.Exists(path) ? new FileInfo(path).Length : 0;
    public bool MicrophoneMissing => audio?.MicrophoneMissing ?? false;

    public void Anchor(long originTicks) => audio?.Anchor(originTicks);
    public void WriteFrame(ID3D11Texture2D frame, long timestampTicks)
    {
        if (Failure is { } error) throw new IOException("Непрерывная запись остановлена", error);
        video.WriteFrame(frame, timestampTicks);
    }

    private unsafe void StoreVideo(IMFSample sample)
    {
        sample.GetSampleTime(out var time);
        sample.GetSampleDuration(out var duration);
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            buffer.Lock(out var pointer, out _, out var length);
            try
            {
                Enqueue(false, new Packet(new ReadOnlySpan<byte>(pointer, (int)length).ToArray(), time, duration,
                    sample.TryGetUInt32(Windows.Win32.PInvoke.MFSampleExtension_CleanPoint) == 1));
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    private void Enqueue(bool sound, Packet packet)
    {
        lock (gate)
        {
            if (Failure is not null) return;
            if (queuedBytes + packet.Data.Length > QueueLimit)
            {
                Interlocked.CompareExchange(ref failure, new IOException("Диск не успевает записывать видео: очередь заполнена"), null);
            }
            else
            {
                (sound ? audioPackets : videoPackets).Enqueue(packet);
                queuedBytes += packet.Data.Length;
            }
        }
        signal.Set();
    }

    private void WriteLoop()
    {
        IMFMediaType? vt = null, at = null;
        try
        {
            while (vt is null || audio is not null && at is null)
            {
                vt ??= video.TryCloneEncodedType();
                at ??= audio?.TryCloneEncodedType();
                lock (gate)
                {
                    if (Failure is not null || finishing && videoPackets.Count == 0) return;
                    if (finishing && vt is not null && at is null && audioPackets.Count == 0) break;
                }
                if (vt is null || audio is not null && at is null) signal.WaitOne(TimeSpan.FromMilliseconds(50));
            }
            using var writer = new CompressedFileWriter(path, vt, at, true);
            var flushedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                Packet? packet = null;
                var sound = false;
                lock (gate)
                {
                    if (Failure is not null) return;
                    if (videoPackets.Count > 0 && (audio is null || audioPackets.Count > 0 || finishing))
                    {
                        sound = audioPackets.Count > 0 && audioPackets.Peek().Time < videoPackets.Peek().Time;
                        packet = (sound ? audioPackets : videoPackets).Dequeue();
                    }
                    else if (audioPackets.Count > 0 && (videoPackets.Count > 0 || finishing))
                    {
                        sound = true;
                        packet = audioPackets.Dequeue();
                    }
                    else if (finishing) break;
                    if (packet is not null) queuedBytes -= packet.Data.Length;
                }
                if (packet is null)
                {
                    signal.WaitOne();
                    continue;
                }
                writer.Write(sound, packet.Data, packet.Time, packet.Duration, packet.Key);
                if (!sound) Volatile.Write(ref end, packet.Time + packet.Duration);
                if (System.Diagnostics.Stopwatch.GetElapsedTime(flushedAt).TotalSeconds >= 1)
                {
                    writer.Flush();
                    flushedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }
            writer.Finish();
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref failure, ex, null);
        }
        finally
        {
            if (vt is not null) Marshal.ReleaseComObject(vt);
            if (at is not null) Marshal.ReleaseComObject(at);
        }
    }

    public void Finish()
    {
        if (finished) return;
        finished = true;
        try { video.Finish(); }
        finally
        {
            try { audio?.Finish(); }
            finally
            {
                lock (gate) finishing = true;
                signal.Set();
                if (!writerThread.Join(TimeSpan.FromSeconds(10)))
                    Interlocked.CompareExchange(ref failure, new TimeoutException("Запись на диск не завершилась за 10 секунд"), null);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Finish(); }
        finally
        {
            if (!writerThread.IsAlive)
            {
                video.Dispose();
                audio?.Dispose();
                signal.Dispose();
            }
        }
    }
}
