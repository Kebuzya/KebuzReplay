using System.Runtime.InteropServices;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Encoding;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Buffering;

internal sealed record ClipWriteResult(
    string Path,
    int Samples,
    int KeyFrames,
    TimeSpan Duration,
    long FileSizeBytes,
    int AudioFrames,
    TimeSpan AudioDuration);

internal static class ClipWriter
{
    public static ClipWriteResult Write(
        string path,
        IMFMediaType encodedType,
        IMFMediaType? audioType,
        ClipSnapshot snapshot)
    {
        MediaFoundationRuntime.EnsureStarted();

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        PInvoke.MFCreateAttributes(out var attributes, 2).ThrowOnFailure();
        attributes.SetGuidValue(PInvoke.MF_TRANSCODE_CONTAINERTYPE, PInvoke.MFTranscodeContainerType_MPEG4);
        attributes.SetUInt32Value(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 1);

        IMFSinkWriter writer;

        try
        {
            PInvoke.MFCreateSinkWriterFromURL(full, null, attributes, out writer).ThrowOnFailure();
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        var origin = snapshot.StartTicks;
        var audio = audioType is null ? null : snapshot.Audio;
        var written = 0;
        var keyFrames = 0;
        var end = 0L;
        var audioFrames = 0;
        var audioEnd = 0L;

        try
        {
            writer.AddStream(encodedType, out var video);
            Describe(writer, video, encodedType);

            var track = 0u;

            if (audio is not null)
            {
                writer.AddStream(audioType!, out track);
                Describe(writer, track, audioType!);
            }

            writer.BeginWriting();

            var next = 0;
            var offset = 0;

            foreach (var segment in snapshot.Segments)
            {
                foreach (var sample in segment.Samples)
                {
                    if (sample.TimestampTicks >= snapshot.LimitTicks)
                    {
                        break;
                    }

                    if (audio is not null)
                    {
                        Flush(writer, track, audio, sample.TimestampTicks, origin, ref next, ref offset, ref audioEnd);
                        audioFrames = next;
                    }

                    var media = Compose(
                        segment.Data.AsSpan(sample.Offset, sample.Length),
                        sample.TimestampTicks - origin,
                        sample.DurationTicks,
                        sample.KeyFrame);

                    try
                    {
                        writer.WriteSample(video, media);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(media);
                    }

                    written++;
                    keyFrames += sample.KeyFrame ? 1 : 0;
                    end = sample.TimestampTicks + sample.DurationTicks - origin;
                }
            }

            if (audio is not null)
            {
                Flush(writer, track, audio, long.MaxValue, origin, ref next, ref offset, ref audioEnd);
                audioFrames = next;
            }

            writer.Finalize();
        }
        finally
        {
            Marshal.ReleaseComObject(writer);
        }

        return new ClipWriteResult(
            full,
            written,
            keyFrames,
            TimeSpan.FromTicks(end),
            new FileInfo(full).Length,
            audioFrames,
            TimeSpan.FromTicks(audioEnd));
    }

    private static void Describe(IMFSinkWriter writer, uint stream, IMFMediaType type)
    {
        var input = VideoMediaTypes.Clone(type);

        try
        {
            writer.SetInputMediaType(stream, input, null);
        }
        finally
        {
            Marshal.ReleaseComObject(input);
        }
    }

    private static void Flush(
        IMFSinkWriter writer,
        uint stream,
        AudioTake audio,
        long boundTicks,
        long origin,
        ref int next,
        ref int offset,
        ref long end)
    {
        while (next < audio.Frames.Count && audio.Frames[next].TimestampTicks <= boundTicks)
        {
            var entry = audio.Frames[next];

            var media = Compose(
                audio.Data.AsSpan(offset, entry.Length),
                entry.TimestampTicks - origin,
                entry.DurationTicks,
                true);

            try
            {
                writer.WriteSample(stream, media);
            }
            finally
            {
                Marshal.ReleaseComObject(media);
            }

            end = entry.EndTicks - origin;
            offset += entry.Length;
            next++;
        }
    }

    private static unsafe IMFSample Compose(ReadOnlySpan<byte> payload, long timeTicks, long durationTicks, bool keyFrame)
    {
        PInvoke.MFCreateMemoryBuffer((uint)payload.Length, out var buffer).ThrowOnFailure();

        try
        {
            buffer.Lock(out var pointer);

            try
            {
                payload.CopyTo(new Span<byte>(pointer, payload.Length));
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength((uint)payload.Length);

            PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
            sample.AddBuffer(buffer);
            sample.SetSampleTime(timeTicks);
            sample.SetSampleDuration(durationTicks);

            if (keyFrame)
            {
                ((IMFAttributes)sample).SetUInt32Value(PInvoke.MFSampleExtension_CleanPoint, 1);
            }

            return sample;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }
}
