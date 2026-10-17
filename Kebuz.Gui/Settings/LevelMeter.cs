using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Kebuz.Recorder.Audio;

namespace Kebuz.Gui.Settings;

public sealed class LevelMeter : INotifyPropertyChanged
{
    public const double FloorDecibels = -60;
    public const double PeakFall = 0.05;

    private const double Step = 0.002;

    private static readonly TimeSpan LimitHold = TimeSpan.FromSeconds(1);

    private double peak;
    private double body;
    private bool limiting;
    private long limitedAt;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Peak => peak;

    public double Body => body;

    public bool Limiting => limiting;

    public void Show(double peakAmplitude, double rmsAmplitude, bool limited = false)
    {
        Show(peakAmplitude, rmsAmplitude, limited, Stopwatch.GetTimestamp());
    }

    public void Show(double peakAmplitude, double rmsAmplitude, bool limited, long timestamp)
    {
        Set(ref peak, Math.Max(Fraction(peakAmplitude), peak - PeakFall), nameof(Peak));
        Set(ref body, Fraction(rmsAmplitude), nameof(Body));

        if (limited)
        {
            limitedAt = timestamp;
        }

        Limit(limitedAt != 0 && Stopwatch.GetElapsedTime(limitedAt, timestamp) < LimitHold);
    }

    public void Clear()
    {
        Set(ref peak, 0, nameof(Peak));
        Set(ref body, 0, nameof(Body));
        limitedAt = 0;
        Limit(false);
    }

    public static double Fraction(double amplitude)
    {
        if (amplitude <= 0)
        {
            return 0;
        }

        return Math.Clamp((AudioGain.Decibels(amplitude) - FloorDecibels) / -FloorDecibels, 0, 1);
    }

    private void Set(ref double field, double value, string name)
    {
        value = Math.Max(value, 0);

        if (Math.Abs(field - value) < Step && !(value is 0 && field is not 0))
        {
            return;
        }

        field = value;
        Raise(name);
    }

    private void Limit(bool value)
    {
        if (limiting == value)
        {
            return;
        }

        limiting = value;
        Raise(nameof(Limiting));
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
