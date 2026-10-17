using System.Globalization;

namespace Kebuz.Recorder.Diagnostics;

public static class Sizes
{
    private const double Kilo = 1024d;
    private const double Mega = Kilo * 1024d;
    private const double Giga = Mega * 1024d;

    public static CultureInfo Culture => Localization.Strings.Culture;

    public static string Format(long bytes)
    {
        if (bytes >= Giga)
        {
            return Localization.Strings.Format("Size.Gigabytes", bytes / Giga);
        }

        if (bytes >= 100 * Mega)
        {
            return Localization.Strings.Format("Size.MegabytesRound", bytes / Mega);
        }

        if (bytes >= Mega)
        {
            return Localization.Strings.Format("Size.Megabytes", bytes / Mega);
        }

        return Localization.Strings.Format("Size.Kilobytes", bytes / Kilo);
    }

    public static string Length(TimeSpan span)
    {
        return string.Create(Culture, $"{(int)span.TotalMinutes}:{span.Seconds:00}");
    }
}
