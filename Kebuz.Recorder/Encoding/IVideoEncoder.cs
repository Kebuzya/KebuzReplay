using Windows.Win32.Graphics.Direct3D11;

namespace Kebuz.Recorder.Encoding;

internal interface IVideoEncoder : IDisposable
{
    void WriteFrame(ID3D11Texture2D frame, long timestampTicks);

    void Anchor(long originTicks)
    {
    }

    void Finish();
}
