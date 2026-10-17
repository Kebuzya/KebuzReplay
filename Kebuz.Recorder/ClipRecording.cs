using System.Diagnostics;
using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Encoding;
using Windows.Graphics.Capture;

namespace Kebuz.Recorder;

internal sealed record RecordingRequest(
    string OutputPath,
    TimeSpan Duration,
    QualityProfile Quality,
    uint? FrameRate,
    uint? BitrateBps,
    string? WindowTitle,
    Action<string>? Trace = null);

internal sealed record RecordingResult(
    VideoEncoderSettings Video,
    EncoderControlReport Controls,
    int FramesCaptured,
    int FramesWritten,
    int FramesDropped,
    TimeSpan Elapsed,
    long FileSizeBytes);

internal static class ClipRecording
{
    public static RecordingResult Run(RecordingRequest request)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture не поддерживается в этой системе");
        }

        var trace = request.Trace ?? (_ => { });

        trace("создаю D3D устройство");
        var device = GraphicsDevice.Create();

        trace("определяю источник захвата");
        var item = ResolveTarget(request.WindowTitle);

        var settings = VideoEncoderSettings.Create(
            (uint)item.Size.Width,
            (uint)item.Size.Height,
            request.Quality,
            request.FrameRate,
            request.BitrateBps);

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        trace($"создаю энкодер {settings.SourceWidth}x{settings.SourceHeight} в {settings.Width}x{settings.Height}");
        var encoder = new VideoEncoder(device, request.OutputPath, settings);
        trace("энкодер готов");

        CaptureResult result;

        try
        {
            result = CaptureLoop.Run(
                device,
                item,
                encoder,
                new CaptureLoopRequest(
                    settings.SourceWidth,
                    settings.SourceHeight,
                    settings.FrameRate,
                    request.Duration,
                    trace));
        }
        finally
        {
            encoder.Dispose();
            device.Dispose();
        }

        trace("файл финализирован");

        var size = new FileInfo(request.OutputPath).Length;
        return new RecordingResult(
            settings,
            encoder.Controls,
            result.FramesCaptured,
            result.FramesWritten,
            result.FramesDropped,
            result.Elapsed,
            size);
    }

    private static GraphicsCaptureItem ResolveTarget(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return CaptureTarget.PrimaryMonitor();
        }

        var hwnd = CaptureTarget.FindWindowByTitle(windowTitle);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"Окно с заголовком, содержащим \"{windowTitle}\", не найдено");
        }

        return CaptureTarget.Window(hwnd);
    }
}
