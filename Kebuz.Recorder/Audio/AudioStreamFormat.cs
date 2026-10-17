using Windows.Win32.Media.Audio;
using Windows.Win32.Media.KernelStreaming;
using Windows.Win32.Media.Multimedia;

namespace Kebuz.Recorder.Audio;

internal readonly record struct AudioStreamFormat(uint SampleRate, ushort Channels, ushort BitsPerSample, bool Float)
{
    private const ushort Extensible = 0xFFFE;
    private const ushort IeeeFloat = 3;
    private const uint FrontPair = 0x3;

    public static readonly AudioStreamFormat Mix = new(48000, 2, 32, true);

    public int BlockAlign => Channels * BitsPerSample / 8;

    public static unsafe AudioStreamFormat Read(WAVEFORMATEX* format)
    {
        var floating = format->wFormatTag switch
        {
            IeeeFloat => true,
            Extensible => ((WAVEFORMATEXTENSIBLE*)format)->SubFormat
                == typeof(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT).GUID,
            _ => false
        };

        return new AudioStreamFormat(
            format->nSamplesPerSec,
            format->nChannels,
            format->wBitsPerSample,
            floating);
    }

    public unsafe WAVEFORMATEXTENSIBLE Describe()
    {
        var described = default(WAVEFORMATEXTENSIBLE);

        described.Format.wFormatTag = Extensible;
        described.Format.nChannels = Channels;
        described.Format.nSamplesPerSec = SampleRate;
        described.Format.wBitsPerSample = BitsPerSample;
        described.Format.nBlockAlign = (ushort)BlockAlign;
        described.Format.nAvgBytesPerSec = SampleRate * (uint)BlockAlign;
        described.Format.cbSize = (ushort)(sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX));
        described.Samples.wValidBitsPerSample = BitsPerSample;
        described.dwChannelMask = Channels is 2 ? FrontPair : (1u << Channels) - 1;
        described.SubFormat = Float
            ? typeof(KSDATAFORMAT_SUBTYPE_IEEE_FLOAT).GUID
            : typeof(KSDATAFORMAT_SUBTYPE_PCM).GUID;

        return described;
    }

    public override string ToString()
    {
        var sample = Float ? $"float{BitsPerSample}" : $"pcm{BitsPerSample}";
        return $"{SampleRate} Гц, каналов {Channels}, {sample}, блок {BlockAlign} байт";
    }
}
