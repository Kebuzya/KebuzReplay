using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Encoding;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Kebuz.Recorder;

internal static class AudioDenoiseProbe
{
    private const uint TestOnly = 1;
    private const int WindowFrames = AudioMixer.SampleRate / 10;
    private const int SearchFrames = AudioMixer.SampleRate / 20;
    private const long StartQpc = 10_000_000_000;

    private static readonly uint[] Rates = [48000, 44100, 32000, 24000, 22050, 16000, 11025, 8000];

    public static void Run(TimeSpan duration, string? device, string? input, string outputBase, Action<string> trace)
    {
        MediaFoundationRuntime.EnsureStarted();

        var raw = input is null ? Record(duration, device, trace) : Load(input);

        if (raw.Length is 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputBase)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(outputBase);
        var seconds = raw.Length / (double)(AudioMixer.SampleRate * AudioMixer.Channels);

        Save(Path.Combine(directory, $"{stem}-raw.wav"), raw);
        Report("как есть", raw);

        var output = new List<float>(raw.Length);
        var worstStamp = 0d;
        var chunks = 0;
        var gaps = 0;
        var watch = new Stopwatch();

        using (var filter = new MicrophoneFilter())
        {
            var random = new Random(7);
            var offset = 0;

            void Take(ReadOnlySpan<float> samples, int frames, long qpc, bool gap)
            {
                var expected = StartQpc + (((output.Count / AudioMixer.Channels) - MicrophoneFilter.LatencyFrames) * TimeSpan.TicksPerSecond / AudioMixer.SampleRate);
                worstStamp = Math.Max(worstStamp, Math.Abs(qpc - expected) / (double)TimeSpan.TicksPerMillisecond);
                chunks++;
                gaps += gap ? 1 : 0;

                foreach (var value in samples)
                {
                    output.Add(value);
                }
            }

            while (offset < raw.Length / AudioMixer.Channels)
            {
                var frames = Math.Min(random.Next(100, 700), (raw.Length / AudioMixer.Channels) - offset);
                var qpc = StartQpc + (offset * TimeSpan.TicksPerSecond / AudioMixer.SampleRate);

                watch.Start();
                filter.Push(
                    raw.AsSpan(offset * AudioMixer.Channels, frames * AudioMixer.Channels),
                    frames,
                    qpc,
                    offset is 0,
                    Take);
                watch.Stop();

                offset += frames;
            }

            Console.WriteLine(
                $"Фильтр: выход ресэмплера {(filter.FloatOutput ? "float" : "pcm16 с переводом")}, " +
                $"кадров на входе {raw.Length / AudioMixer.Channels}, отдано в DSP {filter.InputFrames}, " +
                $"на выходе {filter.OutputFrames}, кусков {chunks}, с флагом разрыва {gaps}");
        }

        var filtered = output.ToArray();

        Console.WriteLine(
            $"Обработка {watch.Elapsed.TotalMilliseconds:F0} мс на {seconds:F1} с звука " +
            $"({watch.Elapsed.TotalSeconds / seconds * 100:F2}% одного ядра), " +
            $"худшее отклонение метки от счёта кадров {worstStamp:F3} мс");

        var lag = Lag(raw, filtered);
        Console.WriteLine(
            lag is { } found
                ? $"Сдвиг выхода относительно входа по корреляции: {found.Frames} кадров " +
                    $"({found.Frames * 1000d / AudioMixer.SampleRate:F1} мс), корреляция {found.Score:F3}, " +
                    $"остаток после поправки {found.Frames - MicrophoneFilter.LatencyFrames} кадров"
                : "Сдвиг по корреляции не найден: слишком коротко");

        Report("шумоподавление", filtered);
        Save(Path.Combine(directory, $"{stem}-ns.wav"), filtered);

        Console.WriteLine($"Файлы: {Path.Combine(directory, stem)}-raw.wav, {stem}-ns.wav");
    }

    public static void Formats()
    {
        MediaFoundationRuntime.EnsureStarted();

        IMFTransform dsp;

        try
        {
            dsp = VoiceCaptureDsp.Create();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Voice Capture DSP как IMFTransform не создался: {ex.Message}");
            return;
        }

        try
        {
            Console.WriteLine("Voice Capture DSP создан как IMFTransform");

            if (dsp is not IPropertyStore store)
            {
                Console.WriteLine("IPropertyStore не отдаёт, настроить режим нечем");
                return;
            }

            Try("режим фильтра", () => VoiceCaptureDsp.SetBool(store, VoiceCaptureDsp.SourceMode, false));
            Try("система: один канал, шумоподавление", () => VoiceCaptureDsp.SetInt(store, VoiceCaptureDsp.SystemMode, (int)AEC_SYSTEM_MODE.SINGLE_CHANNEL_NSAGC));
            Try("ручной выбор функций", () => VoiceCaptureDsp.SetBool(store, VoiceCaptureDsp.FeatureMode, true));
            Try("шумоподавление вкл", () => VoiceCaptureDsp.SetInt(store, VoiceCaptureDsp.NoiseSuppression, 1));
            Try("АРУ выкл", () => VoiceCaptureDsp.SetBool(store, VoiceCaptureDsp.GainControl, false));

            Console.WriteLine(
                $"Прочитано обратно: источник {VoiceCaptureDsp.Read(store, VoiceCaptureDsp.SourceMode)}, " +
                $"система {VoiceCaptureDsp.Read(store, VoiceCaptureDsp.SystemMode)}, " +
                $"функции {VoiceCaptureDsp.Read(store, VoiceCaptureDsp.FeatureMode)}, " +
                $"шумоподавление {VoiceCaptureDsp.Read(store, VoiceCaptureDsp.NoiseSuppression)}, " +
                $"АРУ {VoiceCaptureDsp.Read(store, VoiceCaptureDsp.GainControl)}");

            try
            {
                dsp.GetStreamCount(out var inputs, out var outputs);
                Console.WriteLine($"Потоков: входов {inputs}, выходов {outputs}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetStreamCount: {ex.Message}");
            }

            ListAvailable("Вход 0 предлагает", index => { dsp.GetInputAvailableType(0, index, out var type); return type; });
            ListAvailable("Выход 0 предлагает", index => { dsp.GetOutputAvailableType(0, index, out var type); return type; });

            Console.WriteLine($"Вход 0 принимает (проба): {string.Join(", ", Accepted(type => dsp.SetInputType(0, type, TestOnly)))}");
            Console.WriteLine($"Выход 0 принимает (проба): {string.Join(", ", Accepted(type => dsp.SetOutputType(0, type, TestOnly)))}");
        }
        finally
        {
            Marshal.ReleaseComObject(dsp);
        }
    }

    private static float[] Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(44));
        var result = new float[samples.Length - (samples.Length % AudioMixer.Channels)];

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = samples[i] / 32768f;
        }

        Console.WriteLine($"Вход из файла {path}: {result.Length / AudioMixer.Channels / (double)AudioMixer.SampleRate:F1} с");
        return result;
    }

    private static float[] Record(TimeSpan duration, string? device, Action<string> trace)
    {
        var gate = new Lock();
        var collected = new List<float>(AudioMixer.SampleRate * AudioMixer.Channels * (int)Math.Ceiling(duration.TotalSeconds + 1));
        var activated = AudioEndpoints.Activate(EDataFlow.eCapture, device);

        using var capture = AudioCapture.Open(
            activated.Client,
            activated.Name,
            new AudioCaptureRequest(
                AudioStreamFormat.Mix,
                false,
                false,
                (samples, _, _, flags) =>
                {
                    var values = MemoryMarshal.Cast<byte, float>(samples);

                    lock (gate)
                    {
                        if (flags.HasFlag(AudioPacketFlags.Silent))
                        {
                            collected.AddRange(new float[values.Length]);
                            return;
                        }

                        foreach (var value in values)
                        {
                            collected.Add(value);
                        }
                    }
                },
                trace));

        Console.WriteLine($"Микрофон: {capture.DeviceName}, запись {duration.TotalSeconds:F0} с");
        Console.Out.Flush();

        capture.Start();
        Thread.Sleep(duration);
        capture.Stop();

        if (capture.Failure is { } failure)
        {
            Console.WriteLine($"Захват прерван: {failure.Message}");
            return [];
        }

        lock (gate)
        {
            return collected.ToArray();
        }
    }

    private static (int Frames, double Score)? Lag(float[] raw, float[] filtered)
    {
        var frames = Math.Min(raw.Length, filtered.Length) / AudioMixer.Channels;
        var start = SearchFrames;
        var length = Math.Min(AudioMixer.SampleRate * 2, frames - (SearchFrames * 2));

        if (length < AudioMixer.SampleRate / 2)
        {
            return null;
        }

        double Mono(float[] values, int frame) => (values[frame * 2] + values[(frame * 2) + 1]) / 2d;

        var best = 0;
        var bestScore = double.MinValue;

        for (var lag = -SearchFrames; lag <= SearchFrames; lag++)
        {
            var sum = 0d;
            var left = 0d;
            var right = 0d;

            for (var i = start; i < start + length; i++)
            {
                var a = Mono(raw, i);
                var b = Mono(filtered, i + lag);
                sum += a * b;
                left += a * a;
                right += b * b;
            }

            var score = left > 0 && right > 0 ? sum / Math.Sqrt(left * right) : 0;

            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        return (best, bestScore);
    }

    private static void Try(string what, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  {what}: принято");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {what}: отказ 0x{ex.HResult:X8}");
        }
    }

    private static void ListAvailable(string title, Func<uint, IMFMediaType> get)
    {
        var found = new List<string>();

        for (uint index = 0; index < 64; index++)
        {
            IMFMediaType type;

            try
            {
                type = get(index);
            }
            catch (Exception ex)
            {
                if (index is 0)
                {
                    found.Add($"ничего (0x{ex.HResult:X8})");
                }

                break;
            }

            found.Add(AudioTypes.Describe(type));
            Marshal.ReleaseComObject(type);
        }

        Console.WriteLine($"{title}: {string.Join(", ", found)}");
    }

    private static List<string> Accepted(Action<IMFMediaType> set)
    {
        var accepted = new List<string>();

        foreach (var rate in Rates)
        {
            foreach (var channels in new uint[] { 1, 2 })
            {
                foreach (var floating in new[] { true, false })
                {
                    var type = AudioTypes.Pcm(rate, channels, floating);

                    try
                    {
                        set(type);
                        accepted.Add(AudioTypes.Describe(type));
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(type);
                    }
                }
            }
        }

        if (accepted.Count is 0)
        {
            accepted.Add("ничего");
        }

        return accepted;
    }

    private static void Report(string name, float[] interleaved)
    {
        var windows = new List<double>();
        var span = WindowFrames * AudioMixer.Channels;

        for (var offset = 0; offset + span <= interleaved.Length; offset += span)
        {
            var energy = 0d;

            for (var i = offset; i < offset + span; i += AudioMixer.Channels)
            {
                var mono = (interleaved[i] + interleaved[i + 1]) / 2d;
                energy += mono * mono;
            }

            windows.Add(Math.Sqrt(energy / WindowFrames));
        }

        if (windows.Count is 0)
        {
            Console.WriteLine($"{name}: слишком коротко");
            return;
        }

        windows.Sort();

        string At(double share)
        {
            var value = windows[Math.Min(windows.Count - 1, (int)(windows.Count * share))];
            return value > 0
                ? AudioGain.Decibels(value).ToString("F1", CultureInfo.InvariantCulture)
                : "тишина";
        }

        Console.WriteLine(
            $"{name}: фон (10% тишайших окон по 0,1 с) {At(0.1)} дБ, медиана {At(0.5)} дБ, " +
            $"речь (95%) {At(0.95)} дБ");
    }

    private static void Save(string path, float[] values)
    {
        var samples = new short[values.Length];

        for (var i = 0; i < values.Length; i++)
        {
            samples[i] = (short)(Math.Clamp(values[i], -1f, 1f) * short.MaxValue);
        }

        using var wav = new WavWriter(path, AudioMixer.SampleRate, AudioMixer.Channels);
        wav.Write(samples);
    }
}
