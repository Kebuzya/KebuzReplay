using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Recorder;

internal sealed record AudioMixProbeRequest(
    TimeSpan Duration,
    string OutputPath,
    AudioTrackSettings Sound,
    Action<string> Trace);

internal static class AudioMixProbe
{
    public static void Run(AudioMixProbeRequest request)
    {
        var sources = AudioSourceSet.Gather(new AudioSourceSetRequest(request.Sound, null, request.Trace));

        if (sources.Count is 0)
        {
            Console.WriteLine("Ни одного источника не задано.");
            return;
        }

        var path = Path.GetFullPath(request.OutputPath);
        using var wav = new WavWriter(path, AudioMixer.SampleRate, AudioMixer.Channels);

        using var mixer = new AudioMixer(new AudioMixRequest(
            Qpc.Ticks(),
            sources,
            (samples, _) => wav.Write(samples),
            request.Trace));

        mixer.Start();

        Console.WriteLine($"Микс {request.Duration.TotalSeconds:F0} с в {path}");
        Console.WriteLine();

        foreach (var source in sources)
        {
            Console.WriteLine(
                $"{source.Title}: {(source.State is AudioSourceState.Live ? source.DeviceName : Localization.Strings.Render(source.Complaint))}");
            source.Changed += Announce;
        }

        Thread.Sleep(request.Duration);
        mixer.Stop();

        Report(mixer, sources, wav, path);
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

    private static void Report(AudioMixer mixer, List<AudioSource> sources, WavWriter wav, string path)
    {
        Console.WriteLine();

        foreach (var source in sources)
        {
            var ring = source.Ring;

            Console.WriteLine(
                $"{source.Title}: {(source.State is AudioSourceState.Live ? source.DeviceName : Localization.Strings.Render(source.Complaint))}, " +
                $"громкость {source.Level} (усиление {source.Gain:F3})");

            if (source.Kind is not AudioSourceKind.Process)
            {
                Console.WriteLine(source.Native == AudioStreamFormat.Mix
                    ? "  формат устройства уже общий, приведение не потребовалось"
                    : $"  формат устройства: {source.Native}, приведение сделал WASAPI");
            }

            Console.WriteLine(
                $"  кадров записано {ring.Written}, тишины на разрывах {ring.Gaps}, " +
                $"выброшено {ring.Dropped}, простой {ring.Idle}, правок сетки {ring.Corrections}");
        }

        var seconds = wav.Frames / (double)AudioMixer.SampleRate;
        var size = 44 + (wav.Frames * AudioMixer.Channels * sizeof(short));

        Console.WriteLine();
        Console.WriteLine(
            $"Блоков {mixer.Blocks}, звука {seconds:F2} с, пик {mixer.Peak:F3}, " +
            $"срезано отсчётов {mixer.Clipped}");
        Console.WriteLine($"Уровни до усиления: {mixer.Summary()}");
        Console.WriteLine($"Файл: {path} ({Sizes.Format(size)})");
    }
}
