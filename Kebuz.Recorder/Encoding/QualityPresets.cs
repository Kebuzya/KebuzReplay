namespace Kebuz.Recorder.Encoding;

public enum QualityPreset
{
    Compact,
    Balanced,
    Max,
    Custom
}

public sealed record QualityProfile(
    QualityPreset Preset,
    string TitleKey,
    uint TargetHeight,
    uint FrameRate,
    uint MeanBitrateBps,
    uint PeakBitrateBps,
    uint QualityVsSpeed)
{
    public string Title => Localization.Strings.Get(TitleKey);
}

public static class QualityPresets
{
    public const QualityPreset Default = QualityPreset.Balanced;

    private static readonly QualityProfile[] Profiles =
    [
        new(QualityPreset.Compact, "Quality.Compact", 720, 48, 1_500_000, 3_000_000, 90),
        new(QualityPreset.Balanced, "Quality.Balanced", 810, 48, 2_500_000, 5_000_000, 90),
        new(QualityPreset.Max, "Quality.Max", 0, 60, 8_000_000, 16_000_000, 90)
    ];

    public const uint MinHeight = 360;
    public const uint MaxHeight = 1440;
    public const uint HeightStep = 90;
    public const uint MinFrameRate = 24;
    public const uint MaxFrameRate = 60;
    public const uint MinBitrateKbps = 1000;
    public const uint MaxBitrateKbps = 30000;
    public const uint PeakRatio = 2;

    public static QualityProfile Get(QualityPreset preset)
    {
        foreach (var profile in Profiles)
        {
            if (profile.Preset == preset)
            {
                return profile;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(preset),
            preset,
            "Пресет Custom сам по себе профиля не имеет, брать его через KebuzSettings.Profile");
    }

    public static QualityProfile Resolve(QualityPreset preset, uint height, uint frameRate, uint bitrateKbps)
    {
        return preset is QualityPreset.Custom ? Custom(height, frameRate, bitrateKbps) : Get(preset);
    }

    public static QualityProfile Custom(uint height, uint frameRate, uint bitrateKbps)
    {
        var mean = Math.Clamp(bitrateKbps, MinBitrateKbps, MaxBitrateKbps) * 1000;

        return new QualityProfile(
            QualityPreset.Custom,
            "Quality.Custom",
            Math.Clamp(height, MinHeight, MaxHeight),
            Math.Clamp(frameRate, MinFrameRate, MaxFrameRate),
            mean,
            mean * PeakRatio,
            90);
    }

    public static QualityPreset Parse(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "compact" => QualityPreset.Compact,
            "balanced" => QualityPreset.Balanced,
            "max" => QualityPreset.Max,
            _ => throw new ArgumentException($"Неизвестный пресет качества {text}, ожидалось compact, balanced или max")
        };
    }

    public static (uint Width, uint Height) OutputSize(uint sourceWidth, uint sourceHeight, uint targetHeight)
    {
        if (targetHeight is 0 || sourceHeight <= targetHeight)
        {
            return (Even(sourceWidth), Even(sourceHeight));
        }

        var height = Even(targetHeight);
        var width = Even((uint)Math.Round(sourceWidth * (double)height / sourceHeight));
        return (width, height);
    }

    public static uint Even(uint value)
    {
        return Math.Max(value & ~1u, 2);
    }
}
