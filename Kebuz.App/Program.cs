using Kebuz.App.Supervising;
using Kebuz.Gui;
using Kebuz.Recorder;

namespace Kebuz.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var recorder = args.Contains("--recorder", StringComparer.OrdinalIgnoreCase);
        if (!recorder && args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            return Supervisor.RequestExit();
        }

        if (!DistributionLicenses.AcceptBeforeRunning())
        {
            return 1;
        }

        if (recorder)
        {
            if (!args.Contains("--supervised", StringComparer.OrdinalIgnoreCase))
            {
                ParentConsole.Attach();
            }

            return RecorderHost.Run(args);
        }

        if (args.Contains("--gui", StringComparer.OrdinalIgnoreCase))
        {
            return GuiHost.Run(args);
        }

        ParentConsole.Attach();
        return Supervisor.Run(args);
    }
}
