using System.Globalization;
using System.Runtime.InteropServices;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Capture;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal static class EncoderProbe
{
    private const uint TestOnly = 1;
    private const int NoMoreTypes = unchecked((int)0xC00D36B9);
    private const int TransformTypeNotSet = unchecked((int)0xC00D6D60);
    private const string MediaFormatSuffix = "-0000-0010-8000-00aa00389b71";

    private static readonly (string Name, Guid Subtype)[] Candidates =
    [
        ("ARGB32", PInvoke.MFVideoFormat_ARGB32),
        ("RGB32", PInvoke.MFVideoFormat_RGB32),
        ("NV12", PInvoke.MFVideoFormat_NV12),
        ("IYUV", PInvoke.MFVideoFormat_IYUV),
        ("YUY2", PInvoke.MFVideoFormat_YUY2),
        ("AYUV", PInvoke.MFVideoFormat_AYUV),
        ("P010", PInvoke.MFVideoFormat_P010)
    ];

    private static readonly MFT_ENUM_FLAG AudioEnumFlags =
        MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;

    private static readonly uint[] AudioBitrates = [64_000, 96_000, 128_000, 160_000, 192_000, 256_000];

    private static readonly (string Name, Guid Parameter)[] CodecParameters =
    [
        ("AVEncCommonRateControlMode", EncoderControls.RateControlMode),
        ("AVEncCommonMeanBitRate", EncoderControls.MeanBitRate),
        ("AVEncCommonMaxBitRate", EncoderControls.MaxBitRate),
        ("AVEncCommonQualityVsSpeed", EncoderControls.QualityVsSpeed)
    ];

    public static void Run(QualityProfile quality, uint? frameRate, uint? bitrateBps)
    {
        MediaFoundationRuntime.EnsureStarted();

        using var device = GraphicsDevice.Create();
        var item = CaptureTarget.PrimaryMonitor();
        var settings = VideoEncoderSettings.Create(
            (uint)item.Size.Width,
            (uint)item.Size.Height,
            quality,
            frameRate,
            bitrateBps);

        Console.WriteLine(
            $"Проверка на {settings.SourceWidth}x{settings.SourceHeight} в {settings.Width}x{settings.Height} @ " +
            $"{settings.FrameRate}, пресет {quality.Title}");

        PInvoke.MFCreateDXGIDeviceManager(out var resetToken, out var deviceManager).ThrowOnFailure();
        deviceManager.ResetDevice(device.Device, resetToken);

        var hardware = HardwareEncoder.Enumerate();
        Console.WriteLine($"Аппаратных H264-энкодеров: {hardware.Length}");

        foreach (var activate in hardware)
        {
            try
            {
                Describe(activate, deviceManager, settings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Недоступен: {ex.Message.TrimEnd()} (0x{ex.HResult:X8})");
            }
            finally
            {
                Marshal.ReleaseComObject(activate);
            }
        }

        Marshal.ReleaseComObject(deviceManager);

        DescribeAudioEncoders();
    }

    private static void DescribeAudioEncoders()
    {
        Console.WriteLine();

        var candidates = MediaTransform.Enumerate(
            PInvoke.MFT_CATEGORY_AUDIO_ENCODER,
            AudioEnumFlags,
            PInvoke.MFMediaType_Audio,
            PInvoke.MFAudioFormat_AAC);

        Console.WriteLine($"AAC-энкодеров: {candidates.Length}");

        foreach (var candidate in candidates)
        {
            Console.WriteLine($"  {MediaTransform.FriendlyName(candidate)}");
            Marshal.ReleaseComObject(candidate);
        }

        if (candidates.Length is 0)
        {
            return;
        }

        try
        {
            using var encoder = new AudioEncoder((_, _, _) => { });

            Console.WriteLine();
            Console.WriteLine($"Открыт: {encoder.Name}");
            Console.WriteLine($"  Порядок типов: {encoder.TypeOrder}");
            Console.WriteLine(
                $"  Вход: PCM {AudioMixer.SampleRate} Гц, {AudioMixer.Channels} канала, 16 бит");
            Console.WriteLine("  Битрейты:");

            foreach (var bitrate in AudioBitrates)
            {
                Console.WriteLine($"    {bitrate / 1000d:F0} кбит/с: {encoder.TryBitrate(bitrate)}");
            }

            Console.WriteLine(
                $"  Выходной сэмпл: {encoder.OutputSampleBytes} байт, флаги 0x{encoder.OutputFlags:X}, " +
                $"выделяет {(encoder.OwnsOutputSample ? "MFT" : "вызывающий")}");

            var config = encoder.AudioSpecificConfig();
            Console.WriteLine($"  MF_MT_USER_DATA: {(config.Length is 0 ? "пусто" : Convert.ToHexString(config))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Не открылся: {ex.Message.TrimEnd()} (0x{ex.HResult:X8})");
        }
    }

    private static void Describe(IMFActivate activate, IMFDXGIDeviceManager deviceManager, VideoEncoderSettings settings)
    {
        Console.WriteLine();
        Console.WriteLine(HardwareEncoder.FriendlyName(activate));

        var url = HardwareEncoder.DeviceUrl(activate);
        if (url is not null)
        {
            Console.WriteLine($"  Устройство: {url}");
        }

        var transform = (IMFTransform)activate.ActivateObject(typeof(IMFTransform).GUID);

        try
        {
            transform.GetAttributes(out var attributes);
            var async = attributes.TryGetUInt32(PInvoke.MF_TRANSFORM_ASYNC) ?? 0;
            var d3dAware = attributes.TryGetUInt32(PInvoke.MF_SA_D3D11_AWARE) ?? 0;
            Console.WriteLine($"  Асинхронный: {async != 0}, D3D11-aware: {d3dAware != 0}");

            if (async != 0)
            {
                attributes.SetUInt32Value(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            }

            Marshal.ReleaseComObject(attributes);

            if (d3dAware != 0)
            {
                SetDeviceManager(transform, deviceManager);
            }

            Console.WriteLine("  Входные типы до установки выходного:");
            PrintInputTypes(transform);

            SetOutput(transform, settings);

            Console.WriteLine("  Входные типы после установки выходного:");
            PrintInputTypes(transform);

            Console.WriteLine("  Проверка SetInputType на размере источника:");
            foreach (var (name, subtype) in Candidates)
            {
                Console.WriteLine($"    {name}: {TestInput(transform, subtype, settings.SourceWidth, settings.SourceHeight, settings.FrameRate)}");
            }

            Console.WriteLine($"  Выход {settings.Width}x{settings.Height}, вход на том же размере:");
            SetOutput(transform, settings);
            Console.WriteLine($"    ARGB32: {TestInput(transform, PInvoke.MFVideoFormat_ARGB32, settings.Width, settings.Height, settings.FrameRate)}");
            Console.WriteLine($"    NV12: {TestInput(transform, PInvoke.MFVideoFormat_NV12, settings.Width, settings.Height, settings.FrameRate)}");

            Console.WriteLine($"  Вход {settings.SourceWidth}x{settings.SourceHeight} при выходе {settings.Width}x{settings.Height}:");
            Console.WriteLine($"    ARGB32: {TestInput(transform, PInvoke.MFVideoFormat_ARGB32, settings.SourceWidth, settings.SourceHeight, settings.FrameRate)}");

            PrintCodecApi(transform);
        }
        finally
        {
            activate.ShutdownObject();
            Marshal.ReleaseComObject(transform);
        }
    }

    private static void SetOutput(IMFTransform transform, VideoEncoderSettings settings)
    {
        var type = VideoMediaTypes.CreateH264Output(settings);

        try
        {
            transform.SetOutputType(0, type, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static void PrintInputTypes(IMFTransform transform)
    {
        var index = 0u;

        while (true)
        {
            IMFMediaType type;

            try
            {
                transform.GetInputAvailableType(0, index, out type);
            }
            catch (COMException ex) when (ex.HResult == NoMoreTypes)
            {
                if (index == 0)
                {
                    Console.WriteLine("    Список пуст");
                }

                return;
            }
            catch (COMException ex) when (ex.HResult == TransformTypeNotSet)
            {
                Console.WriteLine("    Недоступны, пока не установлен выходной тип");
                return;
            }

            try
            {
                Console.WriteLine($"    {index}: {Describe(type)}");
            }
            finally
            {
                Marshal.ReleaseComObject(type);
            }

            index++;
        }
    }

    private static void PrintCodecApi(IMFTransform transform)
    {
        var codec = EncoderControls.TryOpen(transform);

        if (codec is null)
        {
            Console.WriteLine("  ICodecAPI: недоступен");
            return;
        }

        Console.WriteLine("  ICodecAPI:");

        foreach (var (name, parameter) in CodecParameters)
        {
            var range = EncoderControls.TryRange(codec, parameter);
            var current = EncoderControls.TryRead(codec, parameter);

            Console.WriteLine(range is null
                ? $"    {name}: диапазон недоступен, текущее {Describe(current)}"
                : $"    {name}: {range.Value.Min}..{range.Value.Max} шаг {range.Value.Step}, " +
                    $"по умолчанию {range.Value.Default}, текущее {Describe(current)}");
        }
    }

    private static string Describe(uint? value)
    {
        return value?.ToString() ?? "недоступно";
    }

    private static string TestInput(
        IMFTransform transform,
        Guid subtype,
        uint width,
        uint height,
        uint frameRate)
    {
        var type = VideoMediaTypes.CreateUncompressedInput(subtype, width, height, frameRate);

        try
        {
            transform.SetInputType(0, type, TestOnly);
            return "принят";
        }
        catch (COMException ex)
        {
            return $"отказ (0x{ex.HResult:X8})";
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static string Describe(IMFMediaType type)
    {
        var subtype = type.TryGetGuid(PInvoke.MF_MT_SUBTYPE);
        var text = subtype is null ? "неизвестный подтип" : DescribeSubtype(subtype.Value);

        var size = type.TryGetUInt64(PInvoke.MF_MT_FRAME_SIZE);
        if (size is not null)
        {
            text += $", {size.Value >> 32}x{size.Value & 0xFFFFFFFF}";
        }

        return text;
    }

    private static string DescribeSubtype(Guid subtype)
    {
        foreach (var (name, candidate) in Candidates)
        {
            if (candidate == subtype)
            {
                return name;
            }
        }

        var text = subtype.ToString();
        if (!text.EndsWith(MediaFormatSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var code = uint.Parse(text[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Span<char> fourcc = stackalloc char[4];

        for (var i = 0; i < 4; i++)
        {
            var value = (char)((code >> (i * 8)) & 0xFF);
            if (value is < ' ' or > '~')
            {
                return $"D3DFMT {code}";
            }

            fourcc[i] = value;
        }

        return new string(fourcc);
    }

    private static unsafe void SetDeviceManager(IMFTransform transform, IMFDXGIDeviceManager deviceManager)
    {
        var pointer = Marshal.GetIUnknownForObject(deviceManager);

        try
        {
            transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

}
