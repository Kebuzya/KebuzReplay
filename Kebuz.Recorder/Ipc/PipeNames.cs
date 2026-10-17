using System.Security.Principal;

namespace Kebuz.Recorder.Ipc;

public static class PipeNames
{
    private static readonly string Sid = CurrentSid();

    public static string RecorderPipe => $"Kebuz.Recorder.{Sid}";

    public static string GuiPipe => $"Kebuz.Gui.{Sid}";

    public static string SupervisorMutex => $"Kebuz.Supervisor.{Sid}";

    public static string ShowEvent => $"Kebuz.Show.{Sid}";

    public static string ExitEvent => $"Kebuz.Exit.{Sid}";

    private static string CurrentSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
