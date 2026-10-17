using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Kebuz.Gui.Shell;

internal static class FullScreenGame
{
    public static bool Exclusive()
    {
        try
        {
            PInvoke.SHQueryUserNotificationState(out var state);
            return state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
