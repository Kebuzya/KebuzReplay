using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Audio;

internal delegate void TransformOutputHandler(ReadOnlySpan<byte> payload);

internal sealed class SyncTransformPump : IDisposable
{
    private const uint StreamId = 0;
    private const uint ProvidesSamples = 0x100;
    private const uint CanProvideSamples = 0x200;
    private const uint MinimumOutputBytes = 64 * 1024;
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);

    private readonly IMFTransform transform;
    private readonly IMFSample? owned;
    private readonly IMFMediaBuffer? ownedBuffer;

    private bool finished;
    private bool disposed;

    public SyncTransformPump(IMFTransform transform)
    {
        this.transform = transform;

        transform.GetOutputStreamInfo(StreamId, out var info);

        if ((info.dwFlags & (ProvidesSamples | CanProvideSamples)) is 0)
        {
            PInvoke.MFCreateMemoryBuffer(Math.Max(info.cbSize, MinimumOutputBytes), out ownedBuffer).ThrowOnFailure();
            PInvoke.MFCreateSample(out owned).ThrowOnFailure();
            owned.AddBuffer(ownedBuffer);
        }

        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    public void Push(ReadOnlySpan<byte> payload, long time, long duration, TransformOutputHandler onOutput)
    {
        var sample = Wrap(payload, time, duration);

        try
        {
            transform.ProcessInput(StreamId, sample, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }

        Drain(onOutput);
    }

    public void Finish(TransformOutputHandler onOutput)
    {
        if (finished)
        {
            return;
        }

        finished = true;

        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0);
        Drain(onOutput);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (owned is not null)
        {
            Marshal.ReleaseComObject(owned);
        }

        if (ownedBuffer is not null)
        {
            Marshal.ReleaseComObject(ownedBuffer);
        }
    }

    private static unsafe IMFSample Wrap(ReadOnlySpan<byte> payload, long time, long duration)
    {
        PInvoke.MFCreateMemoryBuffer((uint)payload.Length, out var buffer).ThrowOnFailure();

        try
        {
            buffer.Lock(out var pointer, out _, out _);

            try
            {
                payload.CopyTo(new Span<byte>(pointer, payload.Length));
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength((uint)payload.Length);

            PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
            sample.AddBuffer(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(duration);
            return sample;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    private unsafe void Drain(TransformOutputHandler onOutput)
    {
        while (true)
        {
            var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
            buffers[0].dwStreamID = StreamId;

            if (owned is not null)
            {
                ownedBuffer!.SetCurrentLength(0);
                buffers[0].pSample = owned;
            }

            try
            {
                transform.ProcessOutput(0, buffers, out _);
            }
            catch (COMException ex) when (ex.HResult == NeedMoreInput)
            {
                return;
            }

            if (buffers[0].pEvents is not null)
            {
                Marshal.ReleaseComObject(buffers[0].pEvents);
            }

            var sample = buffers[0].pSample;

            if (sample is null)
            {
                continue;
            }

            try
            {
                sample.ConvertToContiguousBuffer(out var buffer);

                try
                {
                    buffer.Lock(out var pointer, out _, out var length);

                    try
                    {
                        if (length > 0)
                        {
                            onOutput(new ReadOnlySpan<byte>(pointer, (int)length));
                        }
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
            finally
            {
                if (owned is null)
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
        }
    }
}
