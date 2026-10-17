using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Recorder.Input;

internal sealed class GlobalHotkey : IDisposable
{
    private const int Id = 1;
    private const uint StopMessage = PInvoke.WM_APP;
    private const uint RebindMessage = PInvoke.WM_APP + 1;
    private const int AlreadyRegistered = 1409;

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly Action pressed;
    private readonly Action<Hotkey, string?> changed;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Lock gate = new();

    private Hotkey wanted;
    private Hotkey? attached;
    private uint threadId;
    private Exception? failure;
    private bool disposed;

    public GlobalHotkey(Hotkey hotkey, Action pressed, Action<Hotkey, string?>? changed = null)
    {
        wanted = hotkey;
        this.pressed = pressed;
        this.changed = changed ?? ((_, _) => { });

        thread = new Thread(Pump)
        {
            Name = "KebuzHotkey",
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        ready.Wait();
    }

    public Exception? Failure => failure;

    public Hotkey? Attached
    {
        get
        {
            lock (gate)
            {
                return attached;
            }
        }
    }

    public void Rebind(Hotkey next)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            wanted = next;
        }

        if (threadId != 0)
        {
            PInvoke.PostThreadMessage(threadId, RebindMessage, default, default);
        }
    }

    private void Pump()
    {
        threadId = PInvoke.GetCurrentThreadId();
        PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

        Attach();
        ready.Set();

        try
        {
            Loop();
        }
        finally
        {
            Release();
        }
    }

    private void Attach()
    {
        Hotkey next;

        lock (gate)
        {
            next = wanted;

            if (attached == next)
            {
                return;
            }
        }

        Release();

        var registered = PInvoke.RegisterHotKey(
            HWND.Null,
            Id,
            next.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT,
            (uint)next.Key);

        if (!registered)
        {
            var error = Marshal.GetLastWin32Error();

            changed(next, error == AlreadyRegistered
                ? Localization.Strings.Code("Hotkey.Busy")
                : new Win32Exception(error).Message);

            return;
        }

        lock (gate)
        {
            attached = next;
        }

        changed(next, null);
    }

    private void Release()
    {
        lock (gate)
        {
            if (attached is null)
            {
                return;
            }

            attached = null;
        }

        PInvoke.UnregisterHotKey(HWND.Null, Id);
    }

    private void Loop()
    {
        while (true)
        {
            var result = PInvoke.GetMessage(out var message, HWND.Null, 0, 0);

            if (result.Value <= 0 || message.message == StopMessage)
            {
                return;
            }

            if (message.message == RebindMessage)
            {
                Attach();
                continue;
            }

            if (message.message != PInvoke.WM_HOTKEY || (int)message.wParam.Value != Id)
            {
                continue;
            }

            try
            {
                pressed();
            }
            catch (Exception ex)
            {
                failure = ex;
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        if (threadId != 0)
        {
            PInvoke.PostThreadMessage(threadId, StopMessage, default, default);
        }

        thread.Join(StopTimeout);
        ready.Dispose();
    }
}
