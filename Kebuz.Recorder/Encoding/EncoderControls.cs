using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal sealed record EncoderControlReport(bool Available, IReadOnlyList<string> Details)
{
    public static readonly EncoderControlReport Missing = new(false, []);

    public override string ToString()
    {
        return Available ? string.Join(", ", Details) : "ICodecAPI недоступен";
    }
}

internal static class EncoderControls
{
    public static readonly Guid RateControlMode = typeof(CODECAPI_AVEncCommonRateControlMode).GUID;

    public static readonly Guid MeanBitRate = typeof(CODECAPI_AVEncCommonMeanBitRate).GUID;

    public static readonly Guid MaxBitRate = typeof(CODECAPI_AVEncCommonMaxBitRate).GUID;

    public static readonly Guid QualityVsSpeed = typeof(CODECAPI_AVEncCommonQualityVsSpeed).GUID;

    public static EncoderControlReport Apply(object? encoder, VideoEncoderSettings settings)
    {
        if (TryOpen(encoder) is not { } codec)
        {
            return EncoderControlReport.Missing;
        }

        var details = new List<string>
        {
            Set(codec, "режим", RateControlMode,
                (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_PeakConstrainedVBR),
            Set(codec, "средний", MeanBitRate, settings.BitrateBps),
            Set(codec, "потолок", MaxBitRate, settings.PeakBitrateBps),
            Set(codec, "качество", QualityVsSpeed, settings.QualityVsSpeed)
        };

        return new EncoderControlReport(true, details);
    }

    public static string Verify(object encoder, VideoEncoderSettings settings)
    {
        if (TryOpen(encoder) is not { } codec)
        {
            return "ICodecAPI недоступен";
        }

        var expected = new (string Name, Guid Parameter, uint Value)[]
        {
            ("режим", RateControlMode, (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_PeakConstrainedVBR),
            ("средний", MeanBitRate, settings.BitrateBps),
            ("потолок", MaxBitRate, settings.PeakBitrateBps),
            ("качество", QualityVsSpeed, settings.QualityVsSpeed)
        };

        var details = new List<string>();

        foreach (var (name, parameter, value) in expected)
        {
            var applied = TryRead(codec, parameter);
            details.Add(applied is null
                ? $"{name} нечитаем"
                : applied == value ? $"{name} {value}" : $"{name} ждали {value}, стало {applied}");
        }

        return string.Join(", ", details);
    }

    public static ICodecAPI? TryOpen(object? transform)
    {
        try
        {
            return transform as ICodecAPI;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static (uint Min, uint Max, uint Step, uint Default)? TryRange(ICodecAPI codec, Guid parameter)
    {
        try
        {
            codec.GetParameterRange(parameter, out var min, out var max, out var step);
            codec.GetDefaultValue(parameter, out var value);
            return (Convert.ToUInt32(min), Convert.ToUInt32(max), Convert.ToUInt32(step), Convert.ToUInt32(value));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static uint? TryRead(ICodecAPI codec, Guid parameter)
    {
        try
        {
            codec.GetValue(parameter, out var value);
            return Convert.ToUInt32(value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Set(ICodecAPI codec, string name, Guid parameter, uint value)
    {
        try
        {
            codec.SetValue(parameter, value);
        }
        catch (Exception ex)
        {
            return $"{name} отказ 0x{ex.HResult:X8}";
        }

        var applied = TryRead(codec, parameter);

        if (applied is null)
        {
            return $"{name} принят без проверки";
        }

        return applied == value ? $"{name} {value}" : $"{name} задан {value}, стал {applied}";
    }
}
