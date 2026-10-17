using System.Diagnostics;
using Windows.Graphics.Capture;
using Kebuz.Recorder.Capture;

namespace Kebuz.Recorder;

internal static class CaptureProbe
{
    public static void Run(TimeSpan duration)
    {
        Console.WriteLine($"GraphicsCaptureSession.IsSupported = {GraphicsCaptureSession.IsSupported()}");

        using var device = GraphicsDevice.Create();
        var item = CaptureTarget.PrimaryMonitor();
        Console.WriteLine($"Источник: {item.DisplayName}, {item.Size.Width}x{item.Size.Height}");

        var frames = 0;
        var first = TimeSpan.Zero;
        var last = TimeSpan.Zero;

        using var capture = new FrameCapture(device, item, frame =>
        {
            if (frames == 0)
            {
                first = frame.Timestamp;
                var local = Diagnostics.Qpc.Ticks();
                Console.WriteLine(
                    $"Метка первого кадра {frame.Timestamp.Ticks}, локальный QPC в тот же миг {local}, " +
                    $"разница {TimeSpan.FromTicks(local - frame.Timestamp.Ticks).TotalMilliseconds:F1} мс");
            }

            last = frame.Timestamp;
            frames++;
        });

        capture.Start();
        Thread.Sleep(duration);

        var span = last - first;
        var fps = span > TimeSpan.Zero ? frames / span.TotalSeconds : 0;
        Console.WriteLine($"Кадров получено: {frames} за {span.TotalSeconds:F2} с ({fps:F1} fps)");
    }
}
