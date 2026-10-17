using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Kebuz.Recorder.Capture;

internal sealed class FrameFit : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly ID3D11VideoDevice video;
    private readonly ID3D11VideoContext context;
    private readonly uint width;
    private readonly uint height;
    private readonly Dictionary<ID3D11Texture2D, ID3D11VideoProcessorOutputView> outputs = new(ReferenceEqualityComparer.Instance);

    private ID3D11VideoProcessorEnumerator? enumerator;
    private ID3D11VideoProcessor? processor;
    private ID3D11Texture2D? scratch;
    private ID3D11VideoProcessorInputView? input;
    private uint inputWidth;
    private uint inputHeight;
    private bool disposed;

    public FrameFit(GraphicsDevice device, uint width, uint height)
    {
        this.device = device;
        this.width = width;
        this.height = height;
        video = (ID3D11VideoDevice)device.Device;
        context = (ID3D11VideoContext)device.Context;
    }

    public static bool Same(uint sourceWidth, uint sourceHeight, uint width, uint height)
    {
        return sourceWidth >= width && sourceHeight >= height && sourceWidth - width <= 1 && sourceHeight - height <= 1;
    }

    public static RECT Place(uint sourceWidth, uint sourceHeight, uint width, uint height)
    {
        var scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
        var fitWidth = Math.Min(width, (uint)Math.Round(sourceWidth * scale));
        var fitHeight = Math.Min(height, (uint)Math.Round(sourceHeight * scale));
        var left = (int)((width - fitWidth) / 2);
        var top = (int)((height - fitHeight) / 2);

        return new RECT(left, top, left + (int)fitWidth, top + (int)fitHeight);
    }

    public void Draw(ID3D11Texture2D target, ID3D11Texture2D source, uint sourceWidth, uint sourceHeight)
    {
        Prepare(sourceWidth, sourceHeight);
        device.CopyRegion(scratch!, source, sourceWidth, sourceHeight);

        D3D11_VIDEO_PROCESSOR_STREAM[] streams =
        [
            new D3D11_VIDEO_PROCESSOR_STREAM
            {
                Enable = true,
                pInputSurface = input!
            }
        ];

        context.VideoProcessorBlt(processor!, Output(target), 0, streams);
    }

    private unsafe void Prepare(uint sourceWidth, uint sourceHeight)
    {
        if (processor is not null && sourceWidth == inputWidth && sourceHeight == inputHeight)
        {
            return;
        }

        Release();

        var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputWidth = sourceWidth,
            InputHeight = sourceHeight,
            OutputWidth = width,
            OutputHeight = height,
            InputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            OutputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_OPTIMAL_SPEED
        };

        video.CreateVideoProcessorEnumerator(content, out var createdEnumerator);
        enumerator = createdEnumerator;
        video.CreateVideoProcessor(createdEnumerator, 0, out var createdProcessor);
        processor = createdProcessor;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = sourceWidth,
            Height = sourceHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
        };

        device.Device.CreateTexture2D(desc, null, out var createdScratch);
        scratch = createdScratch;

        var inputDesc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
        {
            ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D
        };

        video.CreateVideoProcessorInputView((ID3D11Resource)createdScratch, createdEnumerator, inputDesc, out var createdInput);
        input = createdInput;

        inputWidth = sourceWidth;
        inputHeight = sourceHeight;

        context.VideoProcessorSetStreamFrameFormat(createdProcessor, 0, D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        context.VideoProcessorSetStreamAutoProcessingMode(createdProcessor, 0, false);
        context.VideoProcessorSetStreamSourceRect(createdProcessor, 0, true, new RECT(0, 0, (int)sourceWidth, (int)sourceHeight));
        context.VideoProcessorSetStreamDestRect(createdProcessor, 0, true, Place(sourceWidth, sourceHeight, width, height));
        context.VideoProcessorSetOutputTargetRect(createdProcessor, true, new RECT(0, 0, (int)width, (int)height));

        var black = new D3D11_VIDEO_COLOR();
        black.RGBA = new D3D11_VIDEO_COLOR_RGBA { R = 0, G = 0, B = 0, A = 1 };
        context.VideoProcessorSetOutputBackgroundColor(createdProcessor, false, black);
    }

    private ID3D11VideoProcessorOutputView Output(ID3D11Texture2D target)
    {
        if (outputs.TryGetValue(target, out var view))
        {
            return view;
        }

        var desc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
        {
            ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D
        };

        video.CreateVideoProcessorOutputView((ID3D11Resource)target, enumerator!, desc, out var created);
        outputs[target] = created;

        return created;
    }

    private void Release()
    {
        foreach (var view in outputs.Values)
        {
            Marshal.ReleaseComObject(view);
        }

        outputs.Clear();

        Drop(ref input);
        Drop(ref scratch);
        Drop(ref processor);
        Drop(ref enumerator);
    }

    private static void Drop<T>(ref T? value) where T : class
    {
        if (value is not null)
        {
            Marshal.ReleaseComObject(value);
            value = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Release();
    }
}
