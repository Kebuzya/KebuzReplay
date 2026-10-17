using System.IO;
using System.Runtime.InteropServices;
using Kebuz.Gui.Thumbs;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Gui.Player;

public sealed record TrimResult(
    string Path,
    double StartSeconds,
    int Samples,
    int KeyFrames,
    TimeSpan Duration,
    int AudioFrames,
    TimeSpan AudioDuration,
    long SizeBytes);

public sealed record SampleInfo(long Ticks, long DurationTicks, bool Key, int Length);

public sealed record ClipLayout(IReadOnlyList<SampleInfo> Video, IReadOnlyList<SampleInfo> Audio)
{
    public IReadOnlyList<double> KeyFrames => [.. Video.Where(sample => sample.Key).Select(sample => sample.Ticks / 10_000_000.0)];
}

public static class ClipTrimmer
{
    private const uint AllStreams = 0xFFFFFFFE;
    private const int NoSuchStream = unchecked((int)0xC00D36B3);
    private const double TicksPerSecond = 10_000_000;

    public static ClipLayout Layout(string path)
    {
        FrameGrabber.Begin();

        var reader = Open(path);

        try
        {
            var (videoIndex, audioIndex) = Streams(reader);

            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(videoIndex, true);

            if (audioIndex is uint chosen)
            {
                reader.SetStreamSelection(chosen, true);
            }

            var video = Collect(reader, videoIndex);
            var audio = audioIndex is uint index ? Collect(reader, index) : [];

            return new ClipLayout(video, audio);
        }
        finally
        {
            Marshal.ReleaseComObject(reader);
        }
    }

    public static TrimResult Cut(string source, string target, double startSeconds, double endSeconds)
    {
        if (endSeconds <= startSeconds)
        {
            throw new ArgumentException("конец отрезка не позже начала");
        }

        FrameGrabber.Begin();

        var reader = Open(source);
        IMFSinkWriter? writer = null;
        AudioTrack? audio = null;
        var done = false;

        try
        {
            var (videoIndex, audioIndex) = Streams(reader);

            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(videoIndex, true);

            if (audioIndex is uint chosen)
            {
                reader.SetStreamSelection(chosen, true);
            }

            writer = Create(target);

            var video = AddStream(writer, reader, videoIndex);
            audio = audioIndex is uint index ? new AudioTrack(index, AddStream(writer, reader, index)) : null;

            writer.BeginWriting();

            var start = (long)Math.Round(startSeconds * TicksPerSecond);
            var end = (long)Math.Round(endSeconds * TicksPerSecond);
            var origin = -1L;
            var videoEnd = 0L;
            var samples = 0;
            var keyFrames = 0;

            while (Next(reader, videoIndex, out var time, out var sample))
            {
                try
                {
                    if (time >= end)
                    {
                        break;
                    }

                    var key = IsKey(sample);

                    if (origin < 0)
                    {
                        if (!key || time < start - HalfFrame(sample))
                        {
                            continue;
                        }

                        origin = time;
                    }

                    audio?.Flush(writer, reader, time, origin);

                    Rebase(sample, time - origin);
                    writer.WriteSample(video, sample);

                    samples++;
                    keyFrames += key ? 1 : 0;
                    sample.GetSampleDuration(out var duration);
                    videoEnd = time + duration;
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }

            if (origin < 0)
            {
                throw new InvalidOperationException("в отрезке нет ключевого кадра");
            }

            audio?.Flush(writer, reader, videoEnd - 1, origin);

            writer.Finalize();
            done = true;

            return new TrimResult(
                target,
                origin / TicksPerSecond,
                samples,
                keyFrames,
                TimeSpan.FromTicks(videoEnd - origin),
                audio?.Frames ?? 0,
                TimeSpan.FromTicks(audio?.End ?? 0),
                new FileInfo(target).Length);
        }
        finally
        {
            audio?.Release();

            if (writer is not null)
            {
                Marshal.ReleaseComObject(writer);
            }

            Marshal.ReleaseComObject(reader);

            if (!done)
            {
                Discard(target);
            }
        }
    }

    private static IMFSourceReader Open(string path)
    {
        PInvoke.MFCreateSourceReaderFromURL(path, null, out var reader).ThrowOnFailure();
        return reader;
    }

    private static IMFSinkWriter Create(string target)
    {
        PInvoke.MFCreateAttributes(out var attributes, 2).ThrowOnFailure();

        try
        {
            attributes.SetGUID(PInvoke.MF_TRANSCODE_CONTAINERTYPE, PInvoke.MFTranscodeContainerType_MPEG4);
            attributes.SetUINT32(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 1);

            PInvoke.MFCreateSinkWriterFromURL(target, null, attributes, out var writer).ThrowOnFailure();

            return writer;
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }
    }

    private static (uint Video, uint? Audio) Streams(IMFSourceReader reader)
    {
        uint? video = null;
        uint? audio = null;

        for (var index = 0u; ; index++)
        {
            IMFMediaType type;

            try
            {
                reader.GetNativeMediaType(index, 0, out type);
            }
            catch (COMException ex) when (ex.HResult == NoSuchStream)
            {
                break;
            }

            try
            {
                type.GetGUID(PInvoke.MF_MT_MAJOR_TYPE, out var major);

                if (major == PInvoke.MFMediaType_Video)
                {
                    video ??= index;
                }
                else if (major == PInvoke.MFMediaType_Audio)
                {
                    audio ??= index;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(type);
            }
        }

        if (video is null)
        {
            throw new InvalidOperationException("в клипе нет видеодорожки");
        }

        return (video.Value, audio);
    }

    private static uint AddStream(IMFSinkWriter writer, IMFSourceReader reader, uint index)
    {
        reader.GetNativeMediaType(index, 0, out var output);
        reader.GetNativeMediaType(index, 0, out var input);

        try
        {
            writer.AddStream(output, out var stream);
            writer.SetInputMediaType(stream, input, null);

            return stream;
        }
        finally
        {
            Marshal.ReleaseComObject(output);
            Marshal.ReleaseComObject(input);
        }
    }

    private static bool Next(IMFSourceReader reader, uint stream, out long time, out IMFSample sample)
    {
        while (true)
        {
            reader.ReadSample(stream, 0, out _, out var flags, out time, out var read);

            if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
            {
                if (read is not null)
                {
                    Marshal.ReleaseComObject(read);
                }

                throw new InvalidOperationException("чтение клипа прервалось с ошибкой");
            }

            if (read is not null)
            {
                sample = read;
                return true;
            }

            if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                sample = null!;
                return false;
            }
        }
    }

    private static List<SampleInfo> Collect(IMFSourceReader reader, uint stream)
    {
        var found = new List<SampleInfo>();

        while (Next(reader, stream, out var time, out var sample))
        {
            try
            {
                sample.GetSampleDuration(out var duration);
                sample.GetTotalLength(out var length);
                found.Add(new SampleInfo(time, duration, IsKey(sample), (int)length));
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }

        return found;
    }

    private static void Rebase(IMFSample sample, long time)
    {
        sample.SetSampleTime(time);
        ((IMFAttributes)sample).DeleteItem(PInvoke.MFSampleExtension_DecodeTimestamp);
    }

    private static bool IsKey(IMFSample sample)
    {
        try
        {
            ((IMFAttributes)sample).GetUINT32(PInvoke.MFSampleExtension_CleanPoint, out var flag);
            return flag != 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static long HalfFrame(IMFSample sample)
    {
        sample.GetSampleDuration(out var duration);
        return duration / 2;
    }

    private static void Discard(string target)
    {
        try
        {
            File.Delete(target);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class AudioTrack(uint index, uint stream)
    {
        private IMFSample? held;
        private long heldTime;
        private bool ended;

        public int Frames { get; private set; }

        public long End { get; private set; }

        public void Flush(IMFSinkWriter writer, IMFSourceReader reader, long boundTicks, long origin)
        {
            while (true)
            {
                if (held is null)
                {
                    if (ended || !Next(reader, index, out heldTime, out var sample))
                    {
                        ended = true;
                        return;
                    }

                    held = sample;
                }

                if (heldTime > boundTicks)
                {
                    return;
                }

                var current = held;
                held = null;

                try
                {
                    if (heldTime < origin)
                    {
                        continue;
                    }

                    current.GetSampleDuration(out var duration);
                    Rebase(current, heldTime - origin);
                    writer.WriteSample(stream, current);

                    Frames++;
                    End = heldTime + duration - origin;
                }
                finally
                {
                    Marshal.ReleaseComObject(current);
                }
            }
        }

        public void Release()
        {
            if (held is not null)
            {
                Marshal.ReleaseComObject(held);
                held = null;
            }
        }
    }
}
