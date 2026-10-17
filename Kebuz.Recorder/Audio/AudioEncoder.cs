using System.Runtime.InteropServices;
using Kebuz.Recorder.Encoding;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Audio;

internal delegate void AudioFrameHandler(ReadOnlySpan<byte> payload, long timestampTicks, long durationTicks);

internal sealed class AudioEncoder : IDisposable
{
    public const uint BitrateBps = 128000;
    public const int FrameSamples = 1024;

    private const uint StreamId = 0;
    private const uint TestOnly = 1;
    private const uint ProfileLevel = 0x29;
    private const uint RawPayload = 0;
    private const uint ProvidesSamples = 0x100;
    private const uint CanProvideSamples = 0x200;
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);

    private static readonly MFT_ENUM_FLAG EnumFlags =
        MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;

    private readonly AudioFrameHandler onFrame;
    private readonly MediaTransform encoder;
    private readonly IMFSample? owned;
    private readonly IMFMediaBuffer? ownedBuffer;

    private IMFMediaType? encodedType;
    private bool finished;
    private bool disposed;

    public AudioEncoder(AudioFrameHandler onFrame)
    {
        this.onFrame = onFrame;

        MediaFoundationRuntime.EnsureStarted();

        encoder = MediaTransform.Open(
            PInvoke.MFT_CATEGORY_AUDIO_ENCODER,
            EnumFlags,
            PInvoke.MFMediaType_Audio,
            PInvoke.MFAudioFormat_AAC,
            "AAC-энкодер");

        TypeOrder = SetTypes();

        encoder.Transform.GetOutputStreamInfo(StreamId, out var info);
        OutputSampleBytes = info.cbSize;
        OutputFlags = info.dwFlags;

        if ((info.dwFlags & (ProvidesSamples | CanProvideSamples)) is 0)
        {
            PInvoke.MFCreateMemoryBuffer(Math.Max(info.cbSize, 1), out ownedBuffer).ThrowOnFailure();
            PInvoke.MFCreateSample(out owned).ThrowOnFailure();
            owned.AddBuffer(ownedBuffer);
        }

        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    public string Name => encoder.Name;

    public string TypeOrder { get; }

    public uint OutputSampleBytes { get; }

    public uint OutputFlags { get; }

    public bool OwnsOutputSample => owned is null;

    public long Frames { get; private set; }

    public long Bytes { get; private set; }

    public IMFMediaType? TryCloneEncodedType()
    {
        return encodedType is null ? null : VideoMediaTypes.Clone(encodedType);
    }

    public string TryBitrate(uint bitrateBps)
    {
        var type = CreateOutputType(bitrateBps);

        try
        {
            encoder.Transform.SetOutputType(StreamId, type, TestOnly);
            return "принят";
        }
        catch (COMException ex)
        {
            return $"отказ (0x{ex.HResult:X8})";
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    public byte[] AudioSpecificConfig()
    {
        CaptureEncodedType();

        return encodedType is null ? [] : TryGetUserData(encodedType);
    }

    public void Write(ReadOnlySpan<short> block, long frame)
    {
        var frames = block.Length / AudioMixer.Channels;
        var time = frame * TimeSpan.TicksPerSecond / AudioMixer.SampleRate;
        var duration = frames * TimeSpan.TicksPerSecond / AudioMixer.SampleRate;
        var sample = Wrap(MemoryMarshal.AsBytes(block), time, duration);

        try
        {
            encoder.Transform.ProcessInput(StreamId, sample, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }

        Drain();
    }

    public void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;

        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0);
        Drain();
        encoder.Transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
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

    private void Drain()
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
                encoder.Transform.ProcessOutput(0, buffers, out _);
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
                CaptureEncodedType();
                Deliver(sample);
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

    private unsafe void Deliver(IMFSample sample)
    {
        sample.GetTotalLength(out var total);

        if (total is 0)
        {
            return;
        }

        long time;
        long duration;

        try
        {
            sample.GetSampleTime(out time);
        }
        catch (COMException)
        {
            time = Frames * FrameSamples * TimeSpan.TicksPerSecond / AudioMixer.SampleRate;
        }

        try
        {
            sample.GetSampleDuration(out duration);
        }
        catch (COMException)
        {
            duration = 0;
        }

        if (duration <= 0)
        {
            duration = FrameSamples * TimeSpan.TicksPerSecond / AudioMixer.SampleRate;
        }

        sample.ConvertToContiguousBuffer(out var buffer);

        try
        {
            buffer.Lock(out var pointer, out _, out var length);

            try
            {
                Frames++;
                Bytes += length;
                onFrame(new ReadOnlySpan<byte>(pointer, (int)length), time, duration);
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

    private void CaptureEncodedType()
    {
        if (encodedType is not null)
        {
            return;
        }

        encoder.Transform.GetOutputCurrentType(StreamId, out encodedType);
    }

    private string SetTypes()
    {
        try
        {
            SetInput();
            SetOutput();
            return "вход, затем выход";
        }
        catch (COMException)
        {
        }

        SetOutput();
        SetInput();
        return "выход, затем вход";
    }

    private void SetInput()
    {
        var type = CreateInputType();

        try
        {
            encoder.Transform.SetInputType(StreamId, type, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private void SetOutput()
    {
        var type = CreateOutputType(BitrateBps);

        try
        {
            encoder.Transform.SetOutputType(StreamId, type, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    public static IMFMediaType CreateInputType()
    {
        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();

        type.SetGuidValue(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
        type.SetGuidValue(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_PCM);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioMixer.SampleRate);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, AudioMixer.Channels);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, AudioMixer.Channels * sizeof(short));
        type.SetUInt32Value(
            PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
            AudioMixer.SampleRate * AudioMixer.Channels * sizeof(short));
        type.SetUInt32Value(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);

        return type;
    }

    public static IMFMediaType CreateOutputType(uint bitrateBps)
    {
        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();

        type.SetGuidValue(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
        type.SetGuidValue(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_AAC);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, AudioMixer.SampleRate);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, AudioMixer.Channels);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        type.SetUInt32Value(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bitrateBps / 8);
        type.SetUInt32Value(PInvoke.MF_MT_AAC_PAYLOAD_TYPE, RawPayload);
        type.SetUInt32Value(PInvoke.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, ProfileLevel);

        return type;
    }

    public static unsafe byte[] TryGetUserData(IMFMediaType type)
    {
        try
        {
            var key = PInvoke.MF_MT_USER_DATA;
            type.GetBlobSize(&key, out var size);

            if (size is 0)
            {
                return [];
            }

            var data = new byte[size];
            type.GetBlob(&key, data, size, null);
            return data;
        }
        catch (COMException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (encodedType is not null)
        {
            Marshal.ReleaseComObject(encodedType);
            encodedType = null;
        }

        if (owned is not null)
        {
            Marshal.ReleaseComObject(owned);
        }

        if (ownedBuffer is not null)
        {
            Marshal.ReleaseComObject(ownedBuffer);
        }

        encoder.Dispose();
    }
}
