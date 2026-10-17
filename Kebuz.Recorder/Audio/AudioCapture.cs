using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace Kebuz.Recorder.Audio;

[Flags]
internal enum AudioPacketFlags
{
    None = 0,
    Discontinuity = 1,
    Silent = 2,
    TimestampError = 4
}

internal delegate void AudioPacketHandler(ReadOnlySpan<byte> samples, int frames, long qpcTicks, AudioPacketFlags flags);

internal sealed record AudioCaptureRequest(
    AudioStreamFormat Format,
    bool Loopback,
    bool EventDriven,
    AudioPacketHandler OnPacket,
    Action<string> Trace);

internal sealed class AudioCapture : IDisposable
{
    private const long BufferDurationTicks = 2_000_000;
    private const uint BufferSilent = 2;
    private const uint BufferDiscontinuity = 1;
    private const uint BufferTimestampError = 4;

    private static readonly TimeSpan MinPoll = TimeSpan.FromMilliseconds(4);
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromMilliseconds(10);

    private readonly AudioCaptureRequest request;
    private readonly IAudioClient client;
    private readonly IAudioCaptureClient reader;
    private readonly ManualResetEvent? ready;
    private readonly ManualResetEventSlim stopping = new(false);
    private readonly TimeSpan poll;
    private readonly Thread pump;

    private Exception? failure;
    private bool started;
    private bool disposed;

    private AudioCapture(
        AudioCaptureRequest request,
        IAudioClient client,
        IAudioCaptureClient reader,
        ManualResetEvent? ready,
        string deviceName,
        TimeSpan period)
    {
        this.request = request;
        this.client = client;
        this.reader = reader;
        this.ready = ready;

        DeviceName = deviceName;
        Period = period;

        poll = period < MinPoll ? MinPoll : period;

        pump = new Thread(Loop)
        {
            Name = "KebuzAudioCapture",
            IsBackground = true
        };

        pump.SetApartmentState(ApartmentState.MTA);
    }

    public AudioStreamFormat Format => request.Format;

    public string DeviceName { get; }

    public TimeSpan Period { get; }

    public Exception? Failure => Volatile.Read(ref failure);

    public static unsafe AudioCapture Open(IAudioClient client, string deviceName, AudioCaptureRequest request)
    {
        ManualResetEvent? ready = null;

        try
        {
            var flags = PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            if (request.Loopback)
            {
                flags |= PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK;
            }

            if (request.EventDriven)
            {
                flags |= PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
            }

            var wanted = request.Format.Describe();

            client.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                flags,
                BufferDurationTicks,
                0,
                (WAVEFORMATEX*)&wanted,
                null);

            if (request.EventDriven)
            {
                ready = new ManualResetEvent(false);
                client.SetEventHandle(new HANDLE(ready.SafeWaitHandle.DangerousGetHandle()));
            }

            client.GetService<IAudioCaptureClient>(out var reader);

            return new AudioCapture(
                request,
                client,
                reader,
                ready,
                deviceName,
                Pace(client));
        }
        catch
        {
            ready?.Dispose();
            throw;
        }
    }

    private static unsafe TimeSpan Pace(IAudioClient client)
    {
        try
        {
            long ticks;
            client.GetDevicePeriod(&ticks, null);
            return TimeSpan.FromTicks(ticks);
        }
        catch (Exception)
        {
            return DefaultPeriod;
        }
    }

    public void Start()
    {
        if (started)
        {
            return;
        }

        started = true;
        client.Start();
        pump.Start();
        request.Trace($"звук пошёл с {DeviceName}, {Format}");
    }

    public void Stop()
    {
        if (!started)
        {
            return;
        }

        started = false;
        stopping.Set();
        pump.Join(TimeSpan.FromSeconds(2));

        try
        {
            client.Stop();
        }
        catch (COMException ex)
        {
            request.Trace($"остановка звука отказала: 0x{ex.HResult:X8}");
        }
    }

    private void Loop()
    {
        try
        {
            while (!stopping.Wait(poll))
            {
                Drain();
            }

            Drain();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref failure, ex);
            request.Trace($"захват звука прерван: {ex.Message}");
        }
    }

    private unsafe void Drain()
    {
        while (true)
        {
            reader.GetNextPacketSize(out var pending);

            if (pending is 0)
            {
                return;
            }

            reader.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc);

            try
            {
                if (frames is 0)
                {
                    return;
                }

                request.OnPacket(
                    new ReadOnlySpan<byte>(data, (int)frames * Format.BlockAlign),
                    (int)frames,
                    (long)qpc,
                    Map(flags));
            }
            finally
            {
                reader.ReleaseBuffer(frames);
            }
        }
    }

    private static AudioPacketFlags Map(uint flags)
    {
        var mapped = AudioPacketFlags.None;

        if ((flags & BufferDiscontinuity) is not 0)
        {
            mapped |= AudioPacketFlags.Discontinuity;
        }

        if ((flags & BufferSilent) is not 0)
        {
            mapped |= AudioPacketFlags.Silent;
        }

        if ((flags & BufferTimestampError) is not 0)
        {
            mapped |= AudioPacketFlags.TimestampError;
        }

        return mapped;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        Stop();
        stopping.Dispose();
        ready?.Dispose();
        Marshal.ReleaseComObject(reader);
        Marshal.ReleaseComObject(client);
    }
}
