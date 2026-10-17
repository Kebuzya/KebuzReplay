using System.Buffers.Binary;
using System.IO.Compression;

namespace Kebuz.Recorder.Capture;

internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] Table = BuildTable();

    public static byte[] Write(byte[] rgb, uint width, uint height)
    {
        if (rgb.Length != width * height * 3)
        {
            throw new ArgumentException($"ожидалось {width * height * 3} байт RGB, пришло {rgb.Length}");
        }

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(output, "IHDR", header);

        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, true))
            {
                var stride = (int)width * 3;

                for (var y = 0; y < height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(rgb, y * stride, stride);
                }
            }

            Chunk(output, "IDAT", packed.ToArray());
        }

        Chunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);

        var name = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(data);

        var crc = Update(0xFFFFFFFFu, name);
        crc = Update(crc, data) ^ 0xFFFFFFFFu;

        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Update(uint crc, byte[] data)
    {
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (var n = 0u; n < 256; n++)
        {
            var c = n;

            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
