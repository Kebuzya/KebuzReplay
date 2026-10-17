using System.Runtime.InteropServices;
using Kebuz.Recorder.Capture;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Kebuz.Recorder.Encoding;

internal sealed class VideoScaler : IDisposable
{
    private const uint StreamId = 0;
    private const uint ProvidesSamples = 0x00000100;
    private const uint CanProvideSamples = 0x00000200;
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);

    private readonly GraphicsDevice device;
    private readonly IMFTransform transform;
    private readonly VideoEncoderSettings settings;
    private readonly DXGI_FORMAT outputFormat;
    private readonly long frameDuration;

    private bool disposed;

    public VideoScaler(
        GraphicsDevice device,
        IMFDXGIDeviceManager deviceManager,
        VideoEncoderSettings settings,
        Guid outputSubtype)
    {
        this.device = device;
        this.settings = settings;

        outputFormat = outputSubtype == PInvoke.MFVideoFormat_NV12
            ? DXGI_FORMAT.DXGI_FORMAT_NV12
            : DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM;

        frameDuration = TimeSpan.TicksPerSecond / settings.FrameRate;

        PInvoke.CoCreateInstance<IMFTransform>(
            PInvoke.CLSID_VideoProcessorMFT,
            null,
            CLSCTX.CLSCTX_INPROC_SERVER,
            out transform).ThrowOnFailure();

        var pointer = Marshal.GetIUnknownForObject(deviceManager);

        try
        {
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }

        SetType(settings.SourceWidth, settings.SourceHeight, PInvoke.MFVideoFormat_ARGB32, input: true);
        SetType(settings.Width, settings.Height, outputSubtype, input: false);

        transform.GetOutputStreamInfo(StreamId, out var info);
        ProvidesOutput = (info.dwFlags & (ProvidesSamples | CanProvideSamples)) != 0;

        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    }

    public bool ProvidesOutput { get; }

    public IMFSample? Scale(IMFSample source, long timestampTicks)
    {
        transform.ProcessInput(StreamId, source, 0);

        IMFSample? first = null;

        while (Drain() is { } output)
        {
            if (first is null)
            {
                first = output;
                continue;
            }

            Marshal.ReleaseComObject(output);
        }

        if (first is null)
        {
            return null;
        }

        first.SetSampleTime(timestampTicks);
        first.SetSampleDuration(frameDuration);
        return first;
    }

    private IMFSample? Drain()
    {
        var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
        buffers[0].dwStreamID = StreamId;

        if (!ProvidesOutput)
        {
            buffers[0].pSample = CreateOutputSample();
        }

        try
        {
            transform.ProcessOutput(0, buffers, out _);
        }
        catch (COMException ex) when (ex.HResult == NeedMoreInput)
        {
            Release(buffers[0].pSample);
            return null;
        }
        catch
        {
            Release(buffers[0].pSample);
            throw;
        }

        if (buffers[0].pEvents is not null)
        {
            Marshal.ReleaseComObject(buffers[0].pEvents);
        }

        return buffers[0].pSample;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
        Marshal.ReleaseComObject(transform);
    }

    private IMFSample CreateOutputSample()
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = settings.Width,
            Height = settings.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = outputFormat,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };

        device.Device.CreateTexture2D(desc, null, out var texture);

        try
        {
            return VideoSamples.FromTexture(texture, 0, frameDuration);
        }
        finally
        {
            Marshal.ReleaseComObject(texture);
        }
    }

    private static void Release(IMFSample? sample)
    {
        if (sample is not null)
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private void SetType(uint width, uint height, Guid subtype, bool input)
    {
        var type = VideoMediaTypes.CreateUncompressedInput(subtype, width, height, settings.FrameRate);

        try
        {
            if (input)
            {
                transform.SetInputType(StreamId, type, 0);
            }
            else
            {
                transform.SetOutputType(StreamId, type, 0);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }
}
