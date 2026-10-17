using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Kebuz.Gui.Localization;
using Kebuz.Gui.Settings;
using Kebuz.Recorder.Localization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Kebuz.Gui.Shell;

public partial class DetectToastWindow : Window
{
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(15);

    private readonly DispatcherTimer life;

    private bool closed;

    public DetectToastWindow()
    {
        InitializeComponent();
        Loc.Track(this, static owner => ((DetectToastWindow)owner).Relocalize());

        life = new DispatcherTimer { Interval = Life };
        life.Tick += OnExpired;

        Closed += OnGone;
    }

    public event Action<FoundGameRow, bool>? Answered;

    public FoundGameRow? Asking => IsVisible ? DataContext as FoundGameRow : null;

    public void Ask(FoundGameRow game)
    {
        if (closed)
        {
            return;
        }

        DataContext = game;
        Relocalize();
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

        var hwnd = new HWND(new WindowInteropHelper(this).Handle);
        var style = PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        PInvoke.SetWindowLong(
            hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            style | (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE);

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

    private void OnAccepted(object sender, RoutedEventArgs e)
    {
        Answer(true);
    }

    private void OnRejected(object sender, RoutedEventArgs e)
    {
        Answer(false);
    }

    private void Answer(bool record)
    {
        var game = DataContext as FoundGameRow;

        Dismiss();

        if (game is not null)
        {
            Answered?.Invoke(game, record);
        }
    }

    private void Relocalize()
    {
        if (DataContext is FoundGameRow game)
        {
            Headline.Text = Strings.Format("Toast.Detect.Headline", game.Title);
        }
    }

    private void Place()
    {
        ToastCorner.Place(this);
    }
}
