using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Recorder.Capture;

internal sealed class ThumbnailHost : IDisposable
{
    private const string ClassName = "KebuzCaptureHost";
    private const string Title = "KebuzReplay: захват без рамки";
    private const uint Activate = 0x0006;
    private const uint RectDestination = 0x1;
    private const uint Opacity = 0x4;
    private const uint Visible = 0x8;
    private const uint SourceClientAreaOnly = 0x10;
    private const uint Quit = 0x0012;
    private const uint Timer = 0x0113;
    private const uint FollowTimer = 1;
    private const uint FollowInterval = 250;

    private static readonly Lock Registration = new();
    private static readonly WNDPROC Procedure = Pass;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, nint> Games = new();
    private static bool registered;

    private readonly nint game;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);

    private uint threadId;
    private nint thumbnail;
    private HWND owner;
    private Exception? failure;
    private bool disposed;

    private ThumbnailHost(nint game)
    {
        this.game = game;
        thread = new Thread(Pump) { IsBackground = true, Name = "KebuzCaptureHost" };
        thread.Start();
        ready.Wait();
    }

    public nint Handle { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public static ThumbnailHost Create(nint game)
    {
        var host = new ThumbnailHost(game);

        if (host.failure is { } failed)
        {
            host.Dispose();
            throw new InvalidOperationException($"окно превью не поднялось: {failed.Message}", failed);
        }

        return host;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        PInvoke.PostThreadMessage(threadId, Quit, default, default);
        thread.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }

    private static unsafe LRESULT Pass(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == Activate && (wParam.Value & 0xFFFF) != 0 && Games.TryGetValue((nint)window.Value, out var game))
        {
            PInvoke.SetForegroundWindow(new HWND((void*)game));
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    private static unsafe void Register(HINSTANCE instance)
    {
        lock (Registration)
        {
            if (registered)
            {
                return;
            }

            fixed (char* name = ClassName)
            {
                var type = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = Procedure,
                    hInstance = instance,
                    lpszClassName = name
                };

                if (PInvoke.RegisterClassEx(type) == 0)
                {
                    throw new InvalidOperationException("класс окна превью не зарегистрирован");
                }
            }

            registered = true;
        }
    }

    private unsafe void Pump()
    {
        threadId = PInvoke.GetCurrentThreadId();

        try
        {
            Open();
        }
        catch (Exception ex)
        {
            failure = ex;
            Close();
            ready.Set();
            return;
        }

        ready.Set();

        while (PInvoke.GetMessage(out var message, default, 0, 0) > 0)
        {
            if (message.message == Timer && (nint)message.hwnd.Value == Handle)
            {
                Follow();
                continue;
            }

            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }

        Close();
    }

    private unsafe void Open()
    {
        var source = new HWND((void*)game);

        if (!PInvoke.GetClientRect(source, out var client) || client.right <= 0 || client.bottom <= 0)
        {
            throw new InvalidOperationException("у окна игры нет клиентской области");
        }

        var origin = new System.Drawing.Point(0, 0);
        PInvoke.ClientToScreen(source, ref origin);

        Width = client.right & ~1;
        Height = client.bottom & ~1;

        var instance = new HINSTANCE((void*)PInvoke.GetModuleHandle((string?)null).DangerousGetHandle());
        Register(instance);

        owner = PInvoke.CreateWindowEx(0, ClassName, Title, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, default, null, null, null);

        var host = PInvoke.CreateWindowEx(
            0,
            ClassName,
            Title,
            WINDOW_STYLE.WS_POPUP,
            origin.X,
            origin.Y,
            Width,
            Height,
            owner,
            null,
            null,
            null);

        if (host.IsNull)
        {
            throw new InvalidOperationException("окно превью не создано");
        }

        Handle = (nint)host.Value;
        Games[Handle] = game;

        var cloak = 1;
        var cloaked = PInvoke.DwmSetWindowAttribute(host, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &cloak, sizeof(int));

        if (cloaked.Failed)
        {
            throw new InvalidOperationException($"окно превью не скрылось, 0x{cloaked.Value:X8}");
        }

        PInvoke.ShowWindow(host, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        var registeredThumbnail = PInvoke.DwmRegisterThumbnail(host, source, out thumbnail);

        if (registeredThumbnail.Failed)
        {
            throw new InvalidOperationException($"DWM не дал превью окна игры, 0x{registeredThumbnail.Value:X8}");
        }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = RectDestination | Opacity | Visible | SourceClientAreaOnly,
            rcDestination = new RECT { left = 0, top = 0, right = Width, bottom = Height },
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true
        };

        var updated = PInvoke.DwmUpdateThumbnailProperties(thumbnail, properties);

        if (updated.Failed)
        {
            throw new InvalidOperationException($"превью окна игры не настроилось, 0x{updated.Value:X8}");
        }

        PInvoke.SetTimer(host, FollowTimer, FollowInterval, null);
    }

    private unsafe void Follow()
    {
        var source = new HWND((void*)game);

        if (!PInvoke.GetClientRect(source, out var client))
        {
            return;
        }

        var width = client.right & ~1;
        var height = client.bottom & ~1;

        if (width <= 0 || height <= 0 || (width == Width && height == Height))
        {
            return;
        }

        var origin = new System.Drawing.Point(0, 0);
        PInvoke.ClientToScreen(source, ref origin);

        var host = new HWND((void*)Handle);
        PInvoke.SetWindowPos(
            host,
            default,
            origin.X,
            origin.Y,
            width,
            height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = RectDestination,
            rcDestination = new RECT { left = 0, top = 0, right = width, bottom = height }
        };

        PInvoke.DwmUpdateThumbnailProperties(thumbnail, properties);

        Width = width;
        Height = height;
    }

    private unsafe void Close()
    {
        if (thumbnail != 0)
        {
            PInvoke.DwmUnregisterThumbnail(thumbnail);
            thumbnail = 0;
        }

        if (Handle != 0)
        {
            Games.TryRemove(Handle, out _);
            PInvoke.DestroyWindow(new HWND((void*)Handle));
        }

        if (!owner.IsNull)
        {
            PInvoke.DestroyWindow(owner);
        }
    }
}
