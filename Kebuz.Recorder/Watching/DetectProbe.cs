using System.Diagnostics;

namespace Kebuz.Recorder.Watching;

internal static class DetectProbe
{
    public static void Run(TimeSpan duration)
    {
        var apps = WatchedApps.Load();
        var ignored = IgnoredApps.Load();

        Console.WriteLine(
            $"Автоопределение {duration.TotalSeconds:F0} с: покрытие от {GameDetector.CoverageShare:P0} " +
            $"или игровая папка, 3D от {GameDetector.GpuShare:F0}% в среднем, держится {GameDetector.SteadyFor.TotalSeconds:F0} с");

        using var detector = new GameDetector();

        var started = Stopwatch.GetTimestamp();
        var cost = TimeSpan.Zero;
        var ticks = 0;

        while (Stopwatch.GetElapsedTime(started) < duration)
        {
            var tick = Stopwatch.GetTimestamp();

            var sample = detector.Tick(apps, ignored, game =>
                Console.WriteLine($"  НАЙДЕНО: {game.Title} ({game.Path})"));

            var spent = Stopwatch.GetElapsedTime(tick);
            cost += spent;
            ticks++;

            Console.WriteLine(
                $"{Stopwatch.GetElapsedTime(started).TotalSeconds,5:F0} с  pid {sample.ProcessId,6}  " +
                $"{sample.Executable ?? "-",-28} покрытие {sample.Coverage,4:P0}  " +
                $"{(sample.GameFolder ? "игровая папка" : "             ")}  " +
                $"3D {(sample.Gpu is { } gpu ? $"{gpu,5:F1}%" : "    -")}  среднее {sample.Mean,5:F1}%  " +
                $"держится {sample.Steady.TotalSeconds,4:F0} с  {spent.TotalMilliseconds,5:F1} мс  {sample.Verdict}");

            Thread.Sleep(1000);
        }

        Console.WriteLine($"Тиков {ticks}, средняя цена тика {(ticks == 0 ? 0 : cost.TotalMilliseconds / ticks):F2} мс");
    }
}
