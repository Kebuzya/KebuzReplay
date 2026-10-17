using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Gallery;

public sealed class CaptureCard
{
    private const int TitleLimit = 48;

    private CaptureCard(CaptureShot shot, ImageSource? image)
    {
        Taken = shot.Taken;
        Shot = image;
        Notice = Strings.Format("Gallery.Capture.Notice", shot.Game);
        Window = $"{Trim(shot.Window)} {shot.Width}x{shot.Height}";
    }

    public DateTime Taken { get; }

    public ImageSource? Shot { get; }

    public string Notice { get; }

    public string Window { get; }

    public string Game => Window;

    public static CaptureCard From(CaptureShot shot)
    {
        return new CaptureCard(shot, Decode(shot.Png));
    }

    private static ImageSource? Decode(byte[]? png)
    {
        if (png is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Error("кадр окна не разобран", ex);
            return null;
        }
    }

    private static string Trim(string title)
    {
        var text = string.IsNullOrWhiteSpace(title) ? Strings.Get("Gallery.Capture.NoTitle") : title.Trim();

        return text.Length <= TitleLimit ? text : text[..(TitleLimit - 1)] + "…";
    }
}
