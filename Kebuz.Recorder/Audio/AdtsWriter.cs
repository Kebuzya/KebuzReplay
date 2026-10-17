namespace Kebuz.Recorder.Audio;

internal sealed class AdtsWriter : IDisposable
{
    private const int HeaderBytes = 7;
    private const int AacLowComplexity = 1;
    private const int Fullness = 0x7FF;

    private static readonly int[] Rates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350
    ];

    private readonly FileStream file;
    private readonly byte[] header = new byte[HeaderBytes];
    private readonly int rateIndex;
    private readonly int channels;

    private bool disposed;

    public AdtsWriter(string path, int sampleRate, int channels)
    {
        rateIndex = Array.IndexOf(Rates, sampleRate);

        if (rateIndex < 0)
        {
            throw new ArgumentException($"ADTS не знает частоту {sampleRate}", nameof(sampleRate));
        }

        this.channels = channels;
        file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public long Frames { get; private set; }

    public long Bytes { get; private set; }

    public void Write(ReadOnlySpan<byte> payload)
    {
        var length = payload.Length + HeaderBytes;

        header[0] = 0xFF;
        header[1] = 0xF1;
        header[2] = (byte)((AacLowComplexity << 6) | (rateIndex << 2) | ((channels >> 2) & 1));
        header[3] = (byte)(((channels & 3) << 6) | ((length >> 11) & 3));
        header[4] = (byte)((length >> 3) & 0xFF);
        header[5] = (byte)(((length & 7) << 5) | ((Fullness >> 6) & 0x1F));
        header[6] = (byte)((Fullness & 0x3F) << 2);

        file.Write(header);
        file.Write(payload);

        Frames++;
        Bytes += length;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        file.Dispose();
    }
}
