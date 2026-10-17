using Kebuz.App.Supervising;
using Kebuz.Gui;
using Kebuz.Recorder;

namespace Kebuz.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--recorder", StringComparer.OrdinalIgnoreCase))
        {
            if (!args.Contains("--supervised", StringComparer.OrdinalIgnoreCase))
            {
                ParentConsole.Attach();
            }

            return RecorderHost.Run(args);
        }

        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            return Supervisor.RequestExit();
        }

        if (args.Contains("--gui", StringComparer.OrdinalIgnoreCase))
        {
            return GuiHost.Run(args);
        }

        ParentConsole.Attach();
        return Supervisor.Run(args);
    }
}
