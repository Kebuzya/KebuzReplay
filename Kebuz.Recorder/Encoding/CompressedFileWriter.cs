using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal sealed class CompressedFileWriter : IDisposable
{
    private readonly IMFSinkWriter writer;
    private readonly IMFMediaSink? sink;
    private readonly IMFByteStream? stream;
    private readonly uint video;
    private readonly uint? audio;
    private bool finished;

    public CompressedFileWriter(string path, IMFMediaType videoType, IMFMediaType? audioType, bool fragmented)
    {
        MediaFoundationRuntime.EnsureStarted();
        PInvoke.MFCreateAttributes(out var attributes, 3).ThrowOnFailure();
        IMFSinkWriter? opened = null;
        try
        {
            attributes.SetUInt32Value(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 1);
            attributes.SetUInt32Value(PInvoke.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            if (fragmented)
            {
                PInvoke.MFCreateFile(MF_FILE_ACCESSMODE.MF_ACCESSMODE_WRITE, MF_FILE_OPENMODE.MF_OPENMODE_FAIL_IF_EXIST,
                    MF_FILE_FLAGS.MF_FILEFLAGS_NONE, path, out stream).ThrowOnFailure();
                PInvoke.MFCreateFMPEG4MediaSink(stream, videoType, audioType, out sink).ThrowOnFailure();
                PInvoke.MFCreateSinkWriterFromMediaSink(sink, attributes, out opened).ThrowOnFailure();
                video = 0;
                audio = audioType is null ? null : 1;
            }
            else
            {
                attributes.SetGuidValue(PInvoke.MF_TRANSCODE_CONTAINERTYPE, PInvoke.MFTranscodeContainerType_MPEG4);
                PInvoke.MFCreateSinkWriterFromURL(path, null, attributes, out opened).ThrowOnFailure();
                opened.AddStream(videoType, out video);
                if (audioType is not null)
                {
                    opened.AddStream(audioType, out var track);
                    audio = track;
                }
            }
            opened.SetInputMediaType(video, videoType, null);
            if (audio is { } audioIndex)
            {
                opened.SetInputMediaType(audioIndex, audioType!, null);
            }
            opened.BeginWriting();
            writer = opened;
        }
        catch
        {
            if (opened is not null) Marshal.ReleaseComObject(opened);
            sink?.Shutdown();
            if (sink is not null) Marshal.ReleaseComObject(sink);
            if (stream is not null) Marshal.ReleaseComObject(stream);
            throw;
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }
    }

    public void Write(bool isAudio, IMFSample sample)
    {
        sample.DeleteItem(PInvoke.MFSampleExtension_DecodeTimestamp);
        writer.WriteSample(isAudio ? audio ?? throw new InvalidOperationException("Нет аудиодорожки") : video, sample);
    }

    public unsafe void Write(bool isAudio, byte[] payload, long time, long duration, bool key)
    {
        PInvoke.MFCreateMemoryBuffer((uint)payload.Length, out var buffer).ThrowOnFailure();
        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            try
            {
                payload.CopyTo(new Span<byte>(pointer, payload.Length));
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength((uint)payload.Length);
            sample.AddBuffer(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(duration);
            sample.SetUInt32Value(PInvoke.MFSampleExtension_CleanPoint, key ? 1u : 0u);
            Write(isAudio, sample);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
    }

    public void Flush() => stream?.Flush();

    public void Finish()
    {
        if (finished) return;
        writer.Finalize();
        finished = true;
    }

    public void Dispose()
    {
        try { Marshal.ReleaseComObject(writer); }
        finally
        {
            try { sink?.Shutdown(); }
            finally
            {
                if (sink is not null) Marshal.ReleaseComObject(sink);
                if (stream is not null) Marshal.ReleaseComObject(stream);
            }
        }
    }
}
