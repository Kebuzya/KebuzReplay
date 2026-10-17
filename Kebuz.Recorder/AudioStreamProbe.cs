using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Recorder;

internal sealed record AudioStreamProbeRequest(
    TimeSpan Duration,
    string? OutputPath,
    double KeepSeconds,
    double RingSeconds,
    AudioTrackSettings Sound,
    Action<string> Trace);

internal static class AudioStreamProbe
{
    public static void Run(AudioStreamProbeRequest request)
    {
        var sources = AudioSourceSet.Gather(
            new AudioSourceSetRequest(request.Sound, null, request.Trace));

        if (sources.Count is 0)
        {
            Console.WriteLine("Ни одного источника не задано.");
            return;
        }

        var keepTicks = (long)(request.KeepSeconds * TimeSpan.TicksPerSecond);

        var window = request.RingSeconds > 0
            ? TimeSpan.FromSeconds(request.RingSeconds)
            : keepTicks > 0
                ? TimeSpan.FromTicks(keepTicks)
                : request.Duration;

        var ring = new AudioRing(AudioRing.EstimateBytes(AudioEncoder.BitrateBps, window, TimeSpan.Zero));

        long firstTime = -1;
        var lastEnd = 0L;
        var maxGap = 0L;
        var maxOverlap = 0L;
        var perSecond = new List<long>();

        using var encoder = new AudioEncoder((payload, time, duration) =>
        {
            if (firstTime < 0)
            {
                firstTime = time;
            }
            else if (time > lastEnd)
            {
                maxGap = Math.Max(maxGap, time - lastEnd);
            }
            else
            {
                maxOverlap = Math.Max(maxOverlap, lastEnd - time);
            }

            lastEnd = time + duration;

            var second = (int)(time / TimeSpan.TicksPerSecond);
            while (perSecond.Count <= second)
            {
                perSecond.Add(0);
            }

            perSecond[second] += payload.Length;

            ring.Append(payload, time, duration);

            if (keepTicks > 0)
            {
                ring.Trim(lastEnd - keepTicks);
            }
        });

        Console.WriteLine($"Энкодер: {encoder.Name}");
        Console.WriteLine($"Порядок типов: {encoder.TypeOrder}, выходной сэмпл {encoder.OutputSampleBytes} Б");
        Console.WriteLine(
            $"AAC {AudioEncoder.BitrateBps / 1000d:F0} кбит/с, {AudioMixer.SampleRate} Гц, " +
            $"{AudioMixer.Channels} канала, {request.Duration.TotalSeconds:F0} с");
        Console.WriteLine(
            $"Кольцо: {Sizes.Format(ring.Status().Capacity)}" +
            (keepTicks > 0 ? $", держу последние {request.KeepSeconds:F0} с" : ", без обрезки"));
        Console.WriteLine();

        using var mixer = new AudioMixer(new AudioMixRequest(
            Qpc.Ticks(),
            sources,
            encoder.Write,
            request.Trace));

        mixer.Start();

        foreach (var source in sources)
        {
            Console.WriteLine(
                $"{source.Title}: {(source.State is AudioSourceState.Live ? source.DeviceName : Localization.Strings.Render(source.Complaint))}");
            source.Changed += Announce;
        }

        Thread.Sleep(request.Duration);
        mixer.Stop();
        encoder.Finish();

        Report(request, encoder, mixer, sources, ring, firstTime, lastEnd, maxGap, maxOverlap, perSecond);
    }

    private static void Announce(AudioSource source)
    {
        Console.WriteLine(source.State switch
        {
            AudioSourceState.Live => $"  {source.Title}: источник на месте, {source.DeviceName}",
            AudioSourceState.Lost => $"  {source.Title}: источник пропал, {Localization.Strings.Render(source.Complaint)}",
            _ => $"  {source.Title}: источник ждёт"
        });
    }

    private static void Report(
        AudioStreamProbeRequest request,
        AudioEncoder encoder,
        AudioMixer mixer,
        List<AudioSource> sources,
        AudioRing ring,
        long firstTime,
        long lastEnd,
        long maxGap,
        long maxOverlap,
        List<long> perSecond)
    {
        Log.Flush();

        var config = encoder.AudioSpecificConfig();
        var stamped = TimeSpan.FromTicks(lastEnd - Math.Max(firstTime, 0));
        var counted = encoder.Frames * AudioEncoder.FrameSamples / (double)AudioMixer.SampleRate;
        var status = ring.Status();

        Console.WriteLine();

        foreach (var source in sources)
        {
            Console.WriteLine(
                $"{source.Title}: кадров записано {source.Ring.Written}, тишины {source.Ring.Gaps}, " +
                $"выброшено {source.Ring.Dropped}, простой {source.Ring.Idle}, правок сетки {source.Ring.Corrections}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Блоков микса: {mixer.Blocks}, кадров PCM: {mixer.Blocks * AudioMixer.BlockFrames}, " +
            $"пик {mixer.Peak:F4}, срезано {mixer.Clipped}");
        Console.WriteLine($"Фреймов AAC: {encoder.Frames}, данных {encoder.Bytes / 1024d:F0} КБ");
        Console.WriteLine(
            $"Длительность по меткам {stamped.TotalSeconds:F3} с, по числу фреймов {counted:F3} с, " +
            $"расхождение {(stamped.TotalSeconds - counted) * 1000:F1} мс");
        Console.WriteLine(
            $"Дыры между фреймами: {TimeSpan.FromTicks(maxGap).TotalMilliseconds:F1} мс, " +
            $"наложения {TimeSpan.FromTicks(maxOverlap).TotalMilliseconds:F1} мс");

        if (stamped > TimeSpan.Zero)
        {
            Console.WriteLine(
                $"Эффективный битрейт: {encoder.Bytes * 8d / 1000d / stamped.TotalSeconds:F1} кбит/с");
        }

        if (perSecond.Count > 2)
        {
            var slice = perSecond.GetRange(1, perSecond.Count - 2);
            Console.WriteLine(
                $"По секундам: минимум {slice.Min() * 8d / 1000d:F1}, максимум {slice.Max() * 8d / 1000d:F1} кбит/с");
        }

        Console.WriteLine(
            $"AudioSpecificConfig: {(config.Length is 0 ? "нет" : Convert.ToHexString(config))} ({config.Length} Б)");
        Console.WriteLine(
            $"Кольцо: фреймов {status.Frames}, занято {Sizes.Format(status.Bytes)} из " +
            $"{Sizes.Format(status.Capacity)}, звука {status.Duration.TotalSeconds:F3} с, " +
            $"обрезано {status.Trimmed}, переполнений {status.Overrun}");

        if (request.OutputPath is null)
        {
            return;
        }

        var path = Path.GetFullPath(request.OutputPath);

        using (var adts = new AdtsWriter(path, AudioMixer.SampleRate, AudioMixer.Channels))
        {
            ring.ForEach((_, payload) => adts.Write(payload));
            Console.WriteLine($"Файл: {path} ({Sizes.Format(adts.Bytes)}, фреймов {adts.Frames})");
        }
    }
}
