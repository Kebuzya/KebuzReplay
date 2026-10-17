using Kebuz.Recorder.Capture;
using Kebuz.Recorder.Diagnostics;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal static class SampleStreamProbe
{
    public static void Run(
        TimeSpan duration,
        QualityProfile quality,
        uint? frameRate,
        uint? bitrateBps,
        Action<string>? trace)
    {
        using var device = GraphicsDevice.Create();
        var item = CaptureTarget.PrimaryMonitor();

        var settings = VideoEncoderSettings.Create(
            (uint)item.Size.Width,
            (uint)item.Size.Height,
            quality,
            frameRate,
            bitrateBps);

        var samples = 0;
        var keyFrames = 0;
        var totalBytes = 0L;
        var largest = 0u;
        long firstTime = -1;
        long lastTime = 0;
        long firstKeyFrameTime = -1;
        long lastKeyFrameTime = -1;
        var maxKeyFrameGap = 0L;
        var perSecond = new List<long>();

        using var encoder = new AsyncVideoEncoder(device, settings, sample =>
        {
            sample.GetTotalLength(out var length);
            sample.GetSampleTime(out var time);
            var keyFrame = ((IMFAttributes)sample).TryGetUInt32(PInvoke.MFSampleExtension_CleanPoint) is 1;

            samples++;
            totalBytes += length;
            largest = Math.Max(largest, length);

            var second = (int)(time / TimeSpan.TicksPerSecond);
            while (perSecond.Count <= second)
            {
                perSecond.Add(0);
            }

            perSecond[second] += length;

            if (firstTime < 0)
            {
                firstTime = time;
            }

            lastTime = time;

            if (!keyFrame)
            {
                return;
            }

            keyFrames++;

            if (firstKeyFrameTime < 0)
            {
                firstKeyFrameTime = time;
            }
            else
            {
                maxKeyFrameGap = Math.Max(maxKeyFrameGap, time - lastKeyFrameTime);
            }

            lastKeyFrameTime = time;
        });

        Console.WriteLine($"Энкодер: {encoder.Name}");
        Console.WriteLine($"Пресет: {quality.Title}");
        Console.WriteLine(
            $"Поток {settings.SourceWidth}x{settings.SourceHeight} в {settings.Width}x{settings.Height} @ " +
            $"{settings.FrameRate}, {duration.TotalSeconds:F0} с");
        Console.WriteLine(
            $"Средний {settings.BitrateBps / 1000d:F0} кбит/с, потолок {settings.PeakBitrateBps / 1000d:F0} кбит/с");
        Console.WriteLine($"Управление: {encoder.Controls}, вход {encoder.InputFormat}, масштабирование {encoder.Scaling}");
        Console.WriteLine($"После согласования типов: {encoder.ControlsAfterTypes}");

        var result = CaptureLoop.Run(
            device,
            item,
            encoder,
            new CaptureLoopRequest(
                settings.SourceWidth,
                settings.SourceHeight,
                settings.FrameRate,
                duration,
                trace ?? (_ => { })));

        var span = TimeSpan.FromTicks(lastTime - Math.Max(firstTime, 0));
        var megabits = totalBytes * 8d / 1_000_000d;

        Log.Flush();

        Console.WriteLine($"Кадров захвачено: {result.FramesCaptured}, отдано энкодеру: {result.FramesWritten}, отброшено: {result.FramesDropped}");
        Console.WriteLine($"Сэмплов получено: {samples}, ключевых: {keyFrames}");
        Console.WriteLine($"Метки времени: {TimeSpan.FromTicks(Math.Max(firstTime, 0)).TotalSeconds:F3} - {TimeSpan.FromTicks(lastTime).TotalSeconds:F3} с");
        Console.WriteLine($"Максимальный интервал между ключевыми: {TimeSpan.FromTicks(maxKeyFrameGap).TotalSeconds:F3} с");
        Console.WriteLine($"Данных: {totalBytes / 1024d:F0} КБ, крупнейший сэмпл {largest / 1024d:F0} КБ");

        if (span > TimeSpan.Zero)
        {
            Console.WriteLine($"Эффективный битрейт: {megabits / span.TotalSeconds:F2} Мбит/с");
        }

        if (perSecond.Count > 2)
        {
            var window = perSecond.GetRange(1, perSecond.Count - 2);
            var peak = window.Max() * 8d / 1_000_000d;
            var floor = window.Min() * 8d / 1_000_000d;
            Console.WriteLine($"По секундам: минимум {floor:F2}, максимум {peak:F2} Мбит/с");
        }
    }
}
