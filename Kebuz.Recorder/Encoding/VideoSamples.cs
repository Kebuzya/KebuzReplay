using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal static class VideoSamples
{
    public static IMFSample FromTexture(ID3D11Texture2D texture, long timestampTicks, long durationTicks)
    {
        PInvoke.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false, out var buffer).ThrowOnFailure();

        try
        {
            ((IMF2DBuffer)buffer).GetContiguousLength(out var length);
            buffer.SetCurrentLength(length);

            PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
            sample.AddBuffer(buffer);
            sample.SetSampleTime(timestampTicks);
            sample.SetSampleDuration(durationTicks);
            return sample;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }
}
