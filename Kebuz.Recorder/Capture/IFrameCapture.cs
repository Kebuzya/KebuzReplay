using Windows.Graphics;

namespace Kebuz.Recorder.Capture;

internal interface IFrameCapture : IDisposable
{
    SizeInt32 Size { get; }

    string Border { get; }

    Exception? Failure { get; }

    void Start(Action<CapturedFrame> onFrame);
}
