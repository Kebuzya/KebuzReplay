using System.Runtime.InteropServices;
using Kebuz.Recorder.Capture;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal sealed class VideoEncoder : IVideoEncoder
{
    private readonly IMFDXGIDeviceManager deviceManager;
    private readonly IMFSinkWriter writer;
    private readonly VideoScaler? scaler;
    private readonly uint streamIndex;
    private readonly long frameDuration;

    private bool finished;
    private bool disposed;

    public VideoEncoder(GraphicsDevice device, string outputPath, VideoEncoderSettings settings)
    {
        frameDuration = TimeSpan.TicksPerSecond / settings.FrameRate;

        MediaFoundationRuntime.EnsureStarted();

        PInvoke.MFCreateDXGIDeviceManager(out var resetToken, out deviceManager).ThrowOnFailure();
        deviceManager.ResetDevice(device.Device, resetToken);

        PInvoke.MFCreateAttributes(out var attributes, 4).ThrowOnFailure();
        attributes.SetUnknownValue(PInvoke.MF_SINK_WRITER_D3D_MANAGER, deviceManager);
        attributes.SetUInt32Value(PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        attributes.SetUInt32Value(PInvoke.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
        attributes.SetGuidValue(PInvoke.MF_TRANSCODE_CONTAINERTYPE, PInvoke.MFTranscodeContainerType_MPEG4);

        PInvoke.MFCreateSinkWriterFromURL(outputPath, null, attributes, out writer).ThrowOnFailure();

        var outputType = VideoMediaTypes.CreateH264Output(settings);
        writer.AddStream(outputType, out streamIndex);
        Marshal.ReleaseComObject(outputType);

        var inputType = VideoMediaTypes.CreateUncompressedInput(
            PInvoke.MFVideoFormat_ARGB32,
            settings.Width,
            settings.Height,
            settings.FrameRate);
        writer.SetInputMediaType(streamIndex, inputType, null);
        Marshal.ReleaseComObject(inputType);

        Controls = EncoderControls.Apply(TryOpenCodecApi(), settings);

        if (settings.Scaled)
        {
            scaler = new VideoScaler(device, deviceManager, settings, PInvoke.MFVideoFormat_ARGB32);
        }

        writer.BeginWriting();
    }

    public EncoderControlReport Controls { get; }

    public void WriteFrame(ID3D11Texture2D frame, long timestampTicks)
    {
        var sample = VideoSamples.FromTexture(frame, timestampTicks, frameDuration);

        try
        {
            if (scaler is null)
            {
                writer.WriteSample(streamIndex, sample);
                return;
            }

            var scaled = scaler.Scale(sample, timestampTicks);

            if (scaled is null)
            {
                return;
            }

            try
            {
                writer.WriteSample(streamIndex, scaled);
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

    private unsafe object? TryOpenCodecApi()
    {
        var service = Guid.Empty;
        var riid = typeof(ICodecAPI).GUID;

        try
        {
            writer.GetServiceForStream(streamIndex, service, riid, out var pointer);

            try
            {
                return Marshal.GetObjectForIUnknown((nint)pointer);
            }
            finally
            {
                Marshal.Release((nint)pointer);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        writer.Finalize();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        scaler?.Dispose();
        Marshal.ReleaseComObject(writer);
        Marshal.ReleaseComObject(deviceManager);
    }
}
