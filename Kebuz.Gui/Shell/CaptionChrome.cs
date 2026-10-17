using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Gui.Shell;

public static class CaptionChrome
{
    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcMouseLeave = 0x02A2;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int HtMaxButton = 9;

    public static readonly DependencyProperty IsMaximizeHoveredProperty =
        DependencyProperty.RegisterAttached(
            "IsMaximizeHovered",
            typeof(bool),
            typeof(CaptionChrome),
            new PropertyMetadata(false));

    public static bool GetIsMaximizeHovered(DependencyObject target)
    {
        return (bool)target.GetValue(IsMaximizeHoveredProperty);
    }

    public static void SetIsMaximizeHovered(DependencyObject target, bool value)
    {
        target.SetValue(IsMaximizeHoveredProperty, value);
    }

    public static void Attach(Window window, Button maximizeButton)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            return;
        }

        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            Handle(window, maximizeButton, hwnd, message, wParam, lParam, ref handled));
    }

    private static IntPtr Handle(
        Window window,
        Button maximizeButton,
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        switch (message)
        {
            case WmGetMinMaxInfo:
                ClampToWorkArea(hwnd, lParam);
                return IntPtr.Zero;

            case WmNcHitTest:
                if (IsOverMaximize(maximizeButton, lParam))
                {
                    if (!GetIsMaximizeHovered(maximizeButton))
                    {
                        SetIsMaximizeHovered(maximizeButton, true);
                        TrackLeave(hwnd);
                    }

                    handled = true;
                    return HtMaxButton;
                }

                SetIsMaximizeHovered(maximizeButton, false);
                return IntPtr.Zero;

            case WmNcMouseMove:
                if (wParam.ToInt32() != HtMaxButton)
                {
                    SetIsMaximizeHovered(maximizeButton, false);
                }

                return IntPtr.Zero;

            case WmNcMouseLeave:
                SetIsMaximizeHovered(maximizeButton, false);
                return IntPtr.Zero;

            case WmNcLButtonDown:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    handled = true;
                }

                return IntPtr.Zero;

            case WmNcLButtonUp:
                if (wParam.ToInt32() == HtMaxButton)
                {
                    Toggle(window);
                    handled = true;
                }

                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
    }

    public static void Toggle(Window window)
    {
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static void TrackLeave(IntPtr hwnd)
    {
        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE | TRACKMOUSEEVENT_FLAGS.TME_NONCLIENT,
            hwndTrack = new HWND(hwnd),
            dwHoverTime = 0
        };

        PInvoke.TrackMouseEvent(ref track);
    }

    private static void ClampToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = PInvoke.MonitorFromWindow(new HWND(hwnd), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor.IsNull)
        {
            return;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var bounds = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        bounds.ptMaxPosition.X = info.rcWork.left - info.rcMonitor.left;
        bounds.ptMaxPosition.Y = info.rcWork.top - info.rcMonitor.top;
        bounds.ptMaxSize.X = info.rcWork.right - info.rcWork.left;
        bounds.ptMaxSize.Y = info.rcWork.bottom - info.rcWork.top;
        Marshal.StructureToPtr(bounds, lParam, true);
    }

    private static bool IsOverMaximize(Button maximizeButton, IntPtr lParam)
    {
        if (!maximizeButton.IsVisible || maximizeButton.ActualWidth <= 0)
        {
            return false;
        }

        var packed = lParam.ToInt64();
        double x = (short)(packed & 0xFFFF);
        double y = (short)((packed >> 16) & 0xFFFF);

        try
        {
            var topLeft = maximizeButton.PointToScreen(new Point(0, 0));
            var bottomRight = maximizeButton.PointToScreen(
                new Point(maximizeButton.ActualWidth, maximizeButton.ActualHeight));

            return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
