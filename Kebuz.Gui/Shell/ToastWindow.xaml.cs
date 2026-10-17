using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using Kebuz.Gui.Gallery;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Shell;

public partial class ToastWindow : Window
{
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer life;

    private bool closed;

    public ToastWindow()
    {
        InitializeComponent();

        life = new DispatcherTimer { Interval = Life };
        life.Tick += OnExpired;

        Closed += OnGone;
    }

    public event Action<ClipItem>? Picked;

    public event Action? Opened;

    public void Announce(ClipItem clip)
    {
        Present(clip, Strings.Get("Toast.ClipSaved"));
    }

    public void Announce(CaptureCard card)
    {
        Present(card, Strings.Format("Toast.CardName", card.Notice, card.Window));
    }

    private void Present(object card, string name)
    {
        if (closed)
        {
            return;
        }

        DataContext = card;
        AutomationProperties.SetName(Frame, name);
        Place();

        if (!IsVisible)
        {
            Show();
        }

        life.Stop();
        life.Start();
    }

    public void Dismiss()
    {
        life.Stop();

        if (!closed)
        {
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Place();
    }

    private void OnGone(object? sender, EventArgs e)
    {
        closed = true;
        life.Stop();
    }

    private void OnExpired(object? sender, EventArgs e)
    {
        Dismiss();
    }

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ClipItem clip)
        {
            Picked?.Invoke(clip);
            return;
        }

        Opened?.Invoke();
    }

    private void Place()
    {
        ToastCorner.Place(this);
    }
}
