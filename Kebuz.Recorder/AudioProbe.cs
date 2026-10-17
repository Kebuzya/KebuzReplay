using System.Runtime.InteropServices;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Diagnostics;
using Windows.Win32.Media.Audio;

namespace Kebuz.Recorder;

internal static class AudioProbe
{
    public static void Run(TimeSpan duration, bool microphone, string? device, Action<string> trace)
    {
        ListEndpoints();

        var flow = microphone ? EDataFlow.eCapture : EDataFlow.eRender;
        var counter = new PacketCounter();
        var activated = AudioEndpoints.Activate(flow, device);

        using var capture = AudioCapture.Open(
            activated.Client,
            activated.Name,
            new AudioCaptureRequest(AudioStreamFormat.Mix, !microphone, false, counter.Take, trace));

        Console.WriteLine();
        Console.WriteLine($"Источник: {capture.DeviceName}{(microphone ? " (микрофон)" : " (loopback)")}");
        Console.WriteLine($"Формат устройства: {activated.Mix}");
        Console.WriteLine($"Запрошено: {capture.Format}");
        Console.WriteLine($"Период устройства: {capture.Period.TotalMilliseconds:F1} мс");

        counter.Format = capture.Format;

        var started = Qpc.Ticks();
        capture.Start();
        Thread.Sleep(duration);
        capture.Stop();
        var finished = Qpc.Ticks();

        if (capture.Failure is { } failure)
        {
            Console.WriteLine($"Захват прерван: {failure.Message}");
            return;
        }

        Report(counter, capture.Format, started, finished);
    }

    private static void ListEndpoints()
    {
        var enumerator = AudioEndpoints.Create();

        try
        {
            Print("Устройства вывода", AudioEndpoints.List(enumerator, EDataFlow.eRender));
            Print("Устройства записи", AudioEndpoints.List(enumerator, EDataFlow.eCapture));
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void Print(string title, IReadOnlyList<AudioEndpoint> endpoints)
    {
        Console.WriteLine($"{title}: {endpoints.Count}");

        foreach (var endpoint in endpoints)
        {
            Console.WriteLine($"  {(endpoint.Default ? "*" : " ")} {endpoint.Name}");
        }
    }

    private static void Report(PacketCounter counter, AudioStreamFormat format, long started, long finished)
    {
        var wall = TimeSpan.FromTicks(finished - started);
        var sound = TimeSpan.FromSeconds(counter.Frames / (double)format.SampleRate);

        Console.WriteLine();
        Console.WriteLine($"Прогон: {wall.TotalSeconds:F2} с, звука получено {sound.TotalSeconds:F2} с");
        Console.WriteLine(
            $"Пакетов: {counter.Packets}, кадров {counter.Frames}, " +
            $"тишиной помечено {counter.Silent}, разрывов {counter.Discontinuities}");

        if (counter.Packets is 0)
        {
            Console.WriteLine("Данных не пришло вовсе: на устройстве вывода ничего не играет.");
            return;
        }

        Console.WriteLine($"Уровень: пик {counter.Peak:F3}, RMS {Decibels(counter.Rms()):F1} дБ");

        var span = TimeSpan.FromTicks(counter.LastQpc - counter.FirstQpc);
        var counted = TimeSpan.FromSeconds((counter.Frames - counter.FirstFrames) / (double)format.SampleRate);

        Console.WriteLine(
            $"Метка первого пакета {counter.FirstQpc}, локальный QPC в тот же миг {counter.FirstLocal}, " +
            $"разница {TimeSpan.FromTicks(counter.FirstLocal - counter.FirstQpc).TotalMilliseconds:F1} мс");
        Console.WriteLine(
            $"Метка последнего пакета {counter.LastQpc}, локальный QPC {counter.LastLocal}, " +
            $"разница {TimeSpan.FromTicks(counter.LastLocal - counter.LastQpc).TotalMilliseconds:F1} мс");
        Console.WriteLine(
            $"По меткам прошло {span.TotalSeconds:F3} с, по числу кадров {counted.TotalSeconds:F3} с, " +
            $"расхождение {(span - counted).TotalMilliseconds:F1} мс");
    }

    private static double Decibels(double amplitude)
    {
        return amplitude <= 0 ? -120 : 20 * Math.Log10(amplitude);
    }

    private sealed class PacketCounter
    {
        private double energy;

        public AudioStreamFormat Format { get; set; }

        public int Packets { get; private set; }

        public long Frames { get; private set; }

        public int Silent { get; private set; }

        public int Discontinuities { get; private set; }

        public double Peak { get; private set; }

        public long FirstQpc { get; private set; }

        public long FirstLocal { get; private set; }

        public long FirstFrames { get; private set; }

        public long LastQpc { get; private set; }

        public long LastLocal { get; private set; }

        public double Rms()
        {
            var values = Frames * Format.Channels;
            return values is 0 ? 0 : Math.Sqrt(energy / values);
        }

        public void Take(ReadOnlySpan<byte> samples, int frames, long qpcTicks, AudioPacketFlags flags)
        {
            var local = Qpc.Ticks();

            if (Packets is 0)
            {
                FirstQpc = qpcTicks;
                FirstLocal = local;
                FirstFrames = frames;
            }

            LastQpc = qpcTicks;
            LastLocal = local;

            Packets++;
            Frames += frames;

            if (flags.HasFlag(AudioPacketFlags.Discontinuity))
            {
                Discontinuities++;
            }

            if (flags.HasFlag(AudioPacketFlags.Silent))
            {
                Silent++;
                return;
            }

            Measure(samples);
        }

        private void Measure(ReadOnlySpan<byte> samples)
        {
            if (Format.Float && Format.BitsPerSample is 32)
            {
                foreach (var value in MemoryMarshal.Cast<byte, float>(samples))
                {
                    Account(value);
                }

                return;
            }

            if (!Format.Float && Format.BitsPerSample is 16)
            {
                foreach (var value in MemoryMarshal.Cast<byte, short>(samples))
                {
                    Account(value / 32768d);
                }
            }
        }

        private void Account(double value)
        {
            energy += value * value;

            var magnitude = Math.Abs(value);
            if (magnitude > Peak)
            {
                Peak = magnitude;
            }
        }
    }
}
