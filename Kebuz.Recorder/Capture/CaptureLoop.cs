using Kebuz.Recorder.Encoding;
using Windows.Graphics.Capture;

namespace Kebuz.Recorder.Capture;

internal sealed record CaptureLoopRequest(
    uint Width,
    uint Height,
    uint FrameRate,
    TimeSpan Duration,
    Action<string> Trace);

internal static class CaptureLoop
{
    public static CaptureResult Run(
        GraphicsDevice device,
        GraphicsCaptureItem item,
        IVideoEncoder encoder,
        CaptureLoopRequest request)
    {
        using var runner = new CaptureRunner(
            device,
            item,
            encoder,
            new CaptureRequest(request.Width, request.Height, request.FrameRate, request.Trace));

        runner.Start();
        Thread.Sleep(request.Duration);

        return runner.Stop();
    }
}
