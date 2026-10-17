using System.Runtime.InteropServices;
using Kebuz.Recorder.Diagnostics;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace Kebuz.Gui.Thumbs;

public sealed record RawFrame(uint Width, uint Height, byte[] Pixels);

public static class FrameGrabber
{
    private const uint ApiVersion = 0x00020070;
    private const uint NoSocket = 1;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint MediaSource = 0xFFFFFFFF;
    private const uint TicksPerSecond = 10_000_000;
    private const double Tail = 0.5;
    private const int Attempts = 64;

    private static readonly object Gate = new();

    private static bool started;

    public static RawFrame? Read(string path, double seconds)
    {
        Begin();

        PInvoke.MFCreateAttributes(out var attributes, 1).ThrowOnFailure();
        attributes.SetUINT32(PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);

        IMFSourceReader? reader = null;

        try
        {
            PInvoke.MFCreateSourceReaderFromURL(path, attributes, out reader).ThrowOnFailure();

            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstVideoStream, true);

            if (!Ask(reader) || !Shape(reader, out var width, out var height, out var stride))
            {
                return null;
            }

            Seek(reader, Bound(reader, seconds));

            return Grab(reader, width, height, stride);
        }
        finally
        {
            if (reader is not null)
            {
                Marshal.ReleaseComObject(reader);
            }

            Marshal.ReleaseComObject(attributes);
        }
    }

    internal static void Begin()
    {
        lock (Gate)
        {
            if (started)
            {
                return;
            }

            PInvoke.MFStartup(ApiVersion, NoSocket).ThrowOnFailure();
            started = true;
        }
    }

    private static bool Ask(IMFSourceReader reader)
    {
        PInvoke.MFCreateMediaType(out var wanted).ThrowOnFailure();

        try
        {
            wanted.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            wanted.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            reader.SetCurrentMediaType(FirstVideoStream, wanted);

            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(wanted);
        }
    }

    private static bool Shape(IMFSourceReader reader, out uint width, out uint height, out int stride)
    {
        width = 0;
        height = 0;
        stride = 0;

        reader.GetCurrentMediaType(FirstVideoStream, out var type);

        try
        {
            type.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out var size);

            width = (uint)(size >> 32);
            height = (uint)size;

            if (width == 0 || height == 0)
            {
                return false;
            }

            stride = Pitch(type, width);

            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    private static int Pitch(IMFMediaType type, uint width)
    {
        try
        {
            type.GetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, out var value);
            var stride = unchecked((int)value);

            if (stride != 0)
            {
                return stride;
            }
        }
        catch (COMException)
        {
        }

        return (int)width * 4;
    }

    private static double Bound(IMFSourceReader reader, double seconds)
    {
        try
        {
            reader.GetPresentationAttribute(MediaSource, PInvoke.MF_PD_DURATION, out var span);

            var length = span.uhVal / (double)TicksPerSecond;

            return length > Tail ? Math.Min(seconds, length - Tail) : 0;
        }
        catch (COMException)
        {
            return seconds;
        }
    }

    private static void Seek(IMFSourceReader reader, double seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        var position = default(PROPVARIANT);
        position.vt = VARENUM.VT_I8;
        position.hVal = (long)(seconds * TicksPerSecond);

        try
        {
            reader.SetCurrentPosition(Guid.Empty, position);
        }
        catch (COMException ex)
        {
            Log.Trace($"миниатюра: перемотка на {seconds:0.000} с отклонена (0x{ex.HResult:X8}), беру первый кадр");
        }
    }

    private static RawFrame? Grab(IMFSourceReader reader, uint width, uint height, int stride)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out _, out var sample);

            if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
            {
                return null;
            }

            if (sample is null)
            {
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    return null;
                }

                continue;
            }

            try
            {
                return Copy(sample, width, height, stride);
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }
        }

        return null;
    }

    private static unsafe RawFrame? Copy(IMFSample sample, uint width, uint height, int stride)
    {
        sample.ConvertToContiguousBuffer(out var buffer);

        try
        {
            buffer.Lock(out var scan, out _, out var length);

            try
            {
                var row = (int)width * 4;
                var pitch = Math.Abs(stride);

                if (pitch < row || (long)pitch * height > length)
                {
                    return null;
                }

                var pixels = new byte[(long)row * height];
                var head = stride < 0 ? scan + (long)pitch * (height - 1) : scan;
                var step = stride < 0 ? -(long)pitch : pitch;

                for (var line = 0u; line < height; line++)
                {
                    var source = new ReadOnlySpan<byte>(head + step * line, row);
                    source.CopyTo(pixels.AsSpan((int)(line * row)));
                }

                return new RawFrame(width, height, pixels);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }
}
