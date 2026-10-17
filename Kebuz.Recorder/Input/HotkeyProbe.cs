using System.Diagnostics;

namespace Kebuz.Recorder.Input;

internal static class HotkeyProbe
{
    public static void Run(Action<string> report)
    {
        var pressed = 0;
        var states = new List<string>();

        void Note(Hotkey combination, string? complaint)
        {
            var line = complaint is null
                ? $"взят {combination}"
                : $"отказ {combination}: {complaint}";

            states.Add(line);
            report(line);
        }

        report("зонд перерегистрации хоткея");

        using var blocker = new GlobalHotkey(Hotkey.Parse("Ctrl+Alt+F9"), () => { });
        report($"блокирующий экземпляр держит {blocker.Attached?.ToString() ?? "ничего"}");

        using var hotkey = new GlobalHotkey(Hotkey.Parse("Ctrl+Alt+F7"), () => Interlocked.Increment(ref pressed), Note);

        Check(report, "первое сочетание взято", hotkey.Attached?.ToString(), "Ctrl+Alt+F7");

        hotkey.Rebind(Hotkey.Parse("Ctrl+Alt+F8"));
        Settle(hotkey, "Ctrl+Alt+F8");
        Check(report, "перерегистрация на свободное", hotkey.Attached?.ToString(), "Ctrl+Alt+F8");

        hotkey.Rebind(Hotkey.Parse("Ctrl+Alt+F9"));
        Settle(hotkey, null);
        Check(report, "занятое сочетание не берётся", hotkey.Attached?.ToString() ?? "ничего", "ничего");
        Check(
            report,
            "отказ назван причиной",
            states.LastOrDefault(),
            "отказ Ctrl+Alt+F9: сочетание занято другой программой");

        hotkey.Rebind(Hotkey.Parse("Ctrl+Alt+F8"));
        Settle(hotkey, "Ctrl+Alt+F8");
        Check(report, "после отказа сочетание снова берётся", hotkey.Attached?.ToString(), "Ctrl+Alt+F8");

        hotkey.Rebind(Hotkey.Parse("Ctrl+Alt+F8"));
        Thread.Sleep(200);
        Check(report, "повтор того же не поднимает событие", states.Count.ToString(), "4");

        Check(report, "обработчик не падал", hotkey.Failure?.Message ?? "нет", "нет");
        report($"нажатий за прогон: {Volatile.Read(ref pressed)}");
    }

    private static void Settle(GlobalHotkey hotkey, string? expected)
    {
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
        {
            if (hotkey.Attached?.ToString() == expected)
            {
                return;
            }

            Thread.Sleep(20);
        }
    }

    private static void Check(Action<string> report, string title, string? actual, string expected)
    {
        report(actual == expected
            ? $"  сходится: {title} = {actual}"
            : $"  РАСХОЖДЕНИЕ: {title}, ожидалось {expected}, получено {actual}");
    }
}
