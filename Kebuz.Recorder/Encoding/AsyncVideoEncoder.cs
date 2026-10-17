using System.Diagnostics;
using System.Runtime.InteropServices;
using Kebuz.Recorder.Capture;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal sealed class AsyncVideoEncoder : IVideoEncoder
{
    private const uint StreamId = 0;
    private const uint NeedInput = 601;
    private const uint HaveOutput = 602;
    private const uint DrainComplete = 603;

    private static readonly TimeSpan CreditTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CreditPoll = TimeSpan.FromMilliseconds(50);

    private readonly Action<IMFSample> onSample;
    private readonly MediaTransform encoder;
    private readonly IMFDXGIDeviceManager deviceManager;
    private readonly IMFMediaEventGenerator events;
    private readonly SemaphoreSlim credits = new(0);
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly Thread pump;
    private readonly long frameDuration;
    private readonly VideoScaler? scaler;

    private readonly Lock typeGate = new();

    private IMFMediaType? encodedType;
    private Exception? failure;
    private bool finished;
    private bool disposed;

    public AsyncVideoEncoder(GraphicsDevice device, VideoEncoderSettings settings, Action<IMFSample> onSample)
    {
        this.onSample = onSample;
        frameDuration = TimeSpan.TicksPerSecond / settings.FrameRate;

        MediaFoundationRuntime.EnsureStarted();

        PInvoke.MFCreateDXGIDeviceManager(out var resetToken, out deviceManager).ThrowOnFailure();
        deviceManager.ResetDevice(device.Device, resetToken);

        encoder = HardwareEncoder.Open();
        Unlock();
        SetDeviceManager();

        Controls = EncoderControls.Apply(encoder.Transform, settings);
        var inputSubtype = SetTypes(settings);
        ControlsAfterTypes = EncoderControls.Verify(encoder.Transform, settings);

        if (settings.Scaled)
        {
            scaler = new VideoScaler(device, deviceManager, settings, inputSubtype);
        }

        InputFormat = inputSubtype == PInvoke.MFVideoFormat_NV12 ? "NV12" : "ARGB32";

        events = (IMFMediaEventGenerator)encoder.Transform;

        pump = new Thread(Loop)
        {
            Name = "KebuzEncoderEvents",
            IsBackground = true
        };

        pump.SetApartmentState(ApartmentState.MTA);
        pump.Start();

        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    public string Name => encoder.Name;

    public EncoderControlReport Controls { get; }

    public string ControlsAfterTypes { get; }

    public string InputFormat { get; }

    public bool Scaling => scaler is not null;

    public IMFMediaType? TryCloneEncodedType()
    {
        lock (typeGate)
        {
            return encodedType is null ? null : VideoMediaTypes.Clone(encodedType);
        }
    }

    public void WriteFrame(ID3D11Texture2D frame, long timestampTicks)
    {
        var sample = VideoSamples.FromTexture(frame, timestampTicks, frameDuration);

        try
        {
            if (scaler is null)
            {
                WaitForCredit();
                encoder.Transform.ProcessInput(StreamId, sample, 0);
                return;
            }

            var scaled = scaler.Scale(sample, timestampTicks);

            if (scaled is null)
            {
                return;
            }

            try
            {
                WaitForCredit();
                encoder.Transform.ProcessInput(StreamId, scaled, 0);
            }
            finally
            {
                Marshal.ReleaseComObject(scaled);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    public void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        ThrowIfFailed();

        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0);

        if (!stopped.Wait(DrainTimeout))
        {
            throw new TimeoutException($"Энкодер не завершил drain за {DrainTimeout.TotalSeconds:F0} с");
        }

        pump.Join(DrainTimeout);
        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        ThrowIfFailed();
    }

    private void Loop()
    {
        try
        {
            while (true)
            {
                events.GetEvent(0, out var mediaEvent);

                uint type;

                try
                {
                    mediaEvent.GetType(out type);
                }
                finally
                {
                    Marshal.ReleaseComObject(mediaEvent);
                }

                switch (type)
                {
                    case NeedInput:
                        credits.Release();
                        break;
                    case HaveOutput:
                        ReadOutput();
                        break;
                    case DrainComplete:
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref failure, ex);
        }
        finally
        {
            stopped.Set();
        }
    }

    private void ReadOutput()
    {
        var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
        buffers[0].dwStreamID = StreamId;

        encoder.Transform.ProcessOutput(0, buffers, out _);

        if (buffers[0].pEvents is not null)
        {
            Marshal.ReleaseComObject(buffers[0].pEvents);
        }

        var sample = buffers[0].pSample;
        if (sample is null)
        {
            return;
        }

        CaptureEncodedType();

        try
        {
            onSample(sample);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private void CaptureEncodedType()
    {
        lock (typeGate)
        {
            if (encodedType is not null)
            {
                return;
            }

            encoder.Transform.GetOutputCurrentType(StreamId, out encodedType);
        }
    }

    private void WaitForCredit()
    {
        var start = Stopwatch.GetTimestamp();

        while (!credits.Wait(CreditPoll))
        {
            ThrowIfFailed();

            if (Stopwatch.GetElapsedTime(start) > CreditTimeout)
            {
                throw new TimeoutException($"Энкодер не запросил кадр за {CreditTimeout.TotalSeconds:F0} с");
            }
        }
    }

    private void Unlock()
    {
        encoder.Transform.GetAttributes(out var attributes);

        try
        {
            if (attributes.TryGetUInt32(PInvoke.MF_TRANSFORM_ASYNC) is not 1)
            {
                throw new InvalidOperationException($"Энкодер {encoder.Name} не асинхронный, ожидалась аппаратная MFT");
            }

            attributes.SetUInt32Value(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            attributes.SetUInt32Value(PInvoke.MF_LOW_LATENCY, 1);
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }
    }

    private void SetDeviceManager()
    {
        var pointer = Marshal.GetIUnknownForObject(deviceManager);

        try
        {
            encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private Guid SetTypes(VideoEncoderSettings settings)
    {
        var outputType = VideoMediaTypes.CreateH264Output(settings);

        try
        {
            encoder.Transform.SetOutputType(StreamId, outputType, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(outputType);
        }

        foreach (var subtype in new[] { PInvoke.MFVideoFormat_ARGB32, PInvoke.MFVideoFormat_NV12 })
        {
            var inputType = VideoMediaTypes.CreateUncompressedInput(
                subtype,
                settings.Width,
                settings.Height,
                settings.FrameRate);

            try
            {
                encoder.Transform.SetInputType(StreamId, inputType, 0);
                return subtype;
            }
            catch (COMException)
            {
            }
            finally
            {
                Marshal.ReleaseComObject(inputType);
            }
        }

        throw new InvalidOperationException(
            $"Энкодер {encoder.Name} не принимает ни ARGB32, ни NV12 на {settings.Width}x{settings.Height}");
    }

    private void ThrowIfFailed()
    {
        var pending = Volatile.Read(ref failure);
        if (pending is not null)
        {
            throw new InvalidOperationException($"Энкодер {encoder.Name} прервал работу", pending);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (!pump.Join(DrainTimeout))
        {
            return;
        }

        lock (typeGate)
        {
            if (encodedType is not null)
            {
                Marshal.ReleaseComObject(encodedType);
                encodedType = null;
            }
        }

        credits.Dispose();
        stopped.Dispose();
        scaler?.Dispose();
        encoder.Dispose();
        Marshal.ReleaseComObject(deviceManager);
    }
}
