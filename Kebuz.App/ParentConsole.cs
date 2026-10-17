using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;

namespace Kebuz.App;

internal static class ParentConsole
{
    public static bool Attached { get; private set; }

    public static void Attach()
    {
        var output = PInvoke.GetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE);

        if (output != HANDLE.Null && output != new HANDLE(-1))
        {
            Attached = true;
            return;
        }

        Attached = PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS);
    }
}
