using System.Runtime.InteropServices;
using Kebuz.Recorder.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Gui.Shell;

internal sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int AppIconId = 32512;
    private const uint IconId = 1;

    private readonly HWND owner;
    private readonly uint taskbarCreated;
    private readonly string tip;
    private readonly HICON icon;
    private readonly bool ownsIcon;
    private readonly Lock gate = new();

    private bool added;
    private bool gone;

    public TrayIcon(nint hwnd, string tip)
    {
        owner = new HWND(hwnd);
        this.tip = tip;
        taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        icon = LoadAppIcon(out ownsIcon);

        Add();

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    public event Action? Opened;

    public event Action? MenuRequested;

    public nint Handle(int message, nint lParam, ref bool handled)
    {
        if (message == taskbarCreated)
        {
            Log.Trace("пришёл TaskbarCreated, добавляю иконку заново");

            lock (gate)
            {
                added = false;
            }

            Add();
            return 0;
        }

        if (message != CallbackMessage)
        {
            return 0;
        }

        handled = true;

        switch ((int)(lParam & 0xFFFF))
        {
            case WmLButtonUp:
                Opened?.Invoke();
                break;

            case WmRButtonUp:
                MenuRequested?.Invoke();
                break;
        }

        return 0;
    }

    public void Foreground()
    {
        PInvoke.SetForegroundWindow(owner);
    }

    public bool TryCursorPosition(out int x, out int y)
    {
        var ok = PInvoke.GetCursorPos(out var cursor);
        x = cursor.X;
        y = cursor.Y;
        return ok;
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        Remove();

        if (ownsIcon && !icon.IsNull)
        {
            PInvoke.DestroyIcon(icon);
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Remove();
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Remove();
    }

    private void Add()
    {
        lock (gate)
        {
            if (gone || added)
            {
                return;
            }

            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = owner,
                uID = IconId,
                uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE
                    | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
                    | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
                uCallbackMessage = CallbackMessage,
                hIcon = icon,
                szTip = tip
            };

            added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data);

            Log.Trace(added
                ? "NIM_ADD принят, иконка в трее"
                : $"NIM_ADD отказал, код {Marshal.GetLastWin32Error()}");
        }
    }

    private void Remove()
    {
        lock (gate)
        {
            gone = true;

            if (!added)
            {
                return;
            }

            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = owner,
                uID = IconId
            };

            var removed = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, data);

            Log.Trace(removed
                ? "NIM_DELETE принят, иконка убрана"
                : $"NIM_DELETE отказал, код {Marshal.GetLastWin32Error()}");

            added = false;
        }
    }

    private static unsafe HICON LoadAppIcon(out bool owned)
    {
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON);
        var module = PInvoke.GetModuleHandle((PCWSTR)null);

        var loaded = PInvoke.LoadImage(
            new HINSTANCE(module.Value),
            new PCWSTR((char*)AppIconId),
            GDI_IMAGE_TYPE.IMAGE_ICON,
            width,
            height,
            IMAGE_FLAGS.LR_DEFAULTCOLOR);

        if (!loaded.IsNull)
        {
            Log.Trace($"иконка программы загружена {width}x{height}");
            owned = true;
            return new HICON(loaded.Value);
        }

        Log.Error($"LoadImage не отдал иконку программы {width}x{height}, код {Marshal.GetLastWin32Error()}");

        var stock = PInvoke.LoadImage(
            HINSTANCE.Null,
            new PCWSTR((char*)AppIconId),
            GDI_IMAGE_TYPE.IMAGE_ICON,
            width,
            height,
            IMAGE_FLAGS.LR_SHARED | IMAGE_FLAGS.LR_DEFAULTCOLOR);

        owned = false;
        return new HICON(stock.Value);
    }
}
