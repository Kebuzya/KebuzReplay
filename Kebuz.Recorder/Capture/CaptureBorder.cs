using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Kebuz.Recorder.Capture;

internal static class CaptureBorder
{
    private const int NoInterface = unchecked((int)0x80004002);
    private const int SetIsBorderRequiredSlot = 7;

    private static readonly Guid SessionWithBorder = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    public static unsafe string TryHide(GraphicsCaptureSession session)
    {
        var unknown = ((IWinRTObject)session).NativeObject.ThisPtr;
        var iid = SessionWithBorder;
        var result = Marshal.QueryInterface(unknown, in iid, out var pointer);

        if (result == NoInterface || pointer == 0)
        {
            return "рамка системная: IsBorderRequired в этой Windows нет";
        }

        if (result != 0)
        {
            return $"рамка системная: запрос IsBorderRequired отказал 0x{result:X8}";
        }

        try
        {
            var table = *(void***)pointer;
            var setter = (delegate* unmanaged[Stdcall]<nint, byte, int>)table[SetIsBorderRequiredSlot];
            var set = setter(pointer, 0);

            return set == 0
                ? "рамка выключена через IsBorderRequired"
                : $"рамка системная: IsBorderRequired = false отказал 0x{set:X8}";
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
