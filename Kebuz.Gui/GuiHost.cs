using System.Windows;
using System.Windows.Threading;
using Kebuz.Gui.Shell;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui;

public static class GuiHost
{
    public static int Run(string[] args)
    {
        Log.Configure(false, false, "kebuz-gui-log.txt");
        Log.Report($"GUI запущен, pid {Environment.ProcessId}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("GUI упал на необработанном исключении", e.ExceptionObject as Exception ?? new Exception($"{e.ExceptionObject}"));
            Log.Flush();
        };

        Strings.Use(Strings.Resolve(StoredLanguage()));

        using var link = AppLink.Connect();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Kebuz.Gui;component/Theme/Controls.xaml")
        });

        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var window = new MainWindow(link);
        int code;

        if (args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            Log.Report("GUI поднят заново после сбоя, окно остаётся в трее");
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            code = app.Run();
        }
        else
        {
            code = app.Run(window);
        }

        Log.Report($"GUI завершается, код {code}");
        Log.Flush();

        return code;
    }

    private static string? StoredLanguage()
    {
        try
        {
            return System.IO.File.Exists(SettingsStore.DefaultPath) ? SettingsStore.Load().UiLanguage : null;
        }
        catch (Exception ex)
        {
            Log.Report($"язык из настроек не прочитан, беру язык Windows: {ex.Message}");
            return null;
        }
    }
}
