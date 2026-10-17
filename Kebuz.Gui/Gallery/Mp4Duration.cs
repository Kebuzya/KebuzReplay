using System.Buffers.Binary;
using System.IO;

namespace Kebuz.Gui.Gallery;

public static class Mp4Duration
{
    public static TimeSpan Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Movie(stream, stream.Length);
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private static TimeSpan Movie(Stream stream, long end)
    {
        foreach (var box in Boxes(stream, stream.Position, end))
        {
            if (box.Type is "moov")
            {
                return Header(stream, box.Start, box.End);
            }
        }

        return TimeSpan.Zero;
    }

    private static TimeSpan Header(Stream stream, long start, long end)
    {
        foreach (var box in Boxes(stream, start, end))
        {
            if (box.Type is not "mvhd")
            {
                continue;
            }

            stream.Position = box.Start;

            var head = new byte[4];

            if (stream.ReadAtLeast(head, 4, false) < 4)
            {
                return TimeSpan.Zero;
            }

            var body = new byte[head[0] is 1 ? 28 : 16];

            if (stream.ReadAtLeast(body, body.Length, false) < body.Length)
            {
                return TimeSpan.Zero;
            }

            var span = body.AsSpan();

            var timescale = head[0] is 1
                ? BinaryPrimitives.ReadUInt32BigEndian(span[16..])
                : BinaryPrimitives.ReadUInt32BigEndian(span[8..]);

            var duration = head[0] is 1
                ? BinaryPrimitives.ReadUInt64BigEndian(span[20..])
                : BinaryPrimitives.ReadUInt32BigEndian(span[12..]);

            return timescale is 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(duration / (double)timescale);
        }

        return TimeSpan.Zero;
    }

    private static IEnumerable<Box> Boxes(Stream stream, long from, long end)
    {
        var position = from;

        while (position + 8 <= end)
        {
            stream.Position = position;

            var header = new byte[8];

            if (stream.ReadAtLeast(header, 8, false) < 8)
            {
                yield break;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var start = position + 8;

            if (size is 1)
            {
                var large = new byte[8];

                if (stream.ReadAtLeast(large, 8, false) < 8)
                {
                    yield break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(large);
                start = position + 16;
            }
            else if (size is 0)
            {
                size = end - position;
            }

            if (size < 8 || position + size > end)
            {
                yield break;
            }

            yield return new Box(type, start, position + size);

            position += size;
        }
    }

    private readonly record struct Box(string Type, long Start, long End);
}
