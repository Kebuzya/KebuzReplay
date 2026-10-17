using System.Runtime.InteropServices;

namespace Kebuz.Recorder.Audio;

internal sealed class WavWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private const ushort Pcm = 1;

    private readonly FileStream file;
    private readonly int channels;
    private readonly int sampleRate;

    private long dataBytes;
    private bool disposed;

    public WavWriter(string path, int sampleRate, int channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;

        file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write(new byte[HeaderBytes]);
    }

    public long Frames => dataBytes / (channels * sizeof(short));

    public void Write(ReadOnlySpan<short> samples)
    {
        var bytes = MemoryMarshal.AsBytes(samples);
        file.Write(bytes);
        dataBytes += bytes.Length;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        file.Position = 0;

        using (var header = new BinaryWriter(file, System.Text.Encoding.ASCII, true))
        {
            header.Write("RIFF"u8);
            header.Write((uint)(HeaderBytes - 8 + dataBytes));
            header.Write("WAVE"u8);
            header.Write("fmt "u8);
            header.Write(16u);
            header.Write(Pcm);
            header.Write((ushort)channels);
            header.Write((uint)sampleRate);
            header.Write((uint)(sampleRate * channels * sizeof(short)));
            header.Write((ushort)(channels * sizeof(short)));
            header.Write((ushort)(sizeof(short) * 8));
            header.Write("data"u8);
            header.Write((uint)dataBytes);
        }

        file.Dispose();
    }
}
