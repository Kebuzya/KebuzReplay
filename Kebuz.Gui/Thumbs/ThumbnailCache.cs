using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Gui.Thumbs;

internal static class ThumbnailCache
{
    private const int BoxWidth = 320;
    private const int BoxHeight = 180;
    private const int Quality = 80;
    private const string Suffix = ".jpg";

    public static string Root => Path.Combine(Kebuz.Recorder.Settings.AppPaths.Local, "thumbs");

    public static string FileOf(string clip)
    {
        return Path.Combine(Root, Key(clip) + Suffix);
    }

    public static ImageSource? Take(string clip, double seconds)
    {
        var file = FileOf(clip);

        if (Open(file) is { } kept)
        {
            return kept;
        }

        if (!File.Exists(clip))
        {
            Log.Trace($"миниатюра: файла уже нет, пропускаю {clip}");
            return null;
        }

        if (FrameGrabber.Read(clip, seconds) is not { } frame)
        {
            return null;
        }

        var data = Encode(Shrink(frame));
        Write(file, data);

        return Decode(data);
    }

    public static void Forget(string clip)
    {
        var file = FileOf(clip);

        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"миниатюра не удалилась: {file}", ex);
        }
    }

    public static void Sweep(IReadOnlyCollection<string> alive)
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var clip in alive)
            {
                kept.Add(Key(clip) + Suffix);
            }

            var gone = 0;

            foreach (var file in Directory.EnumerateFiles(Root, "*" + Suffix))
            {
                if (kept.Contains(Path.GetFileName(file)))
                {
                    continue;
                }

                File.Delete(file);
                gone++;
            }

            if (gone > 0)
            {
                Log.Report($"из кэша миниатюр убрано файлов: {gone}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("не удалось вычистить кэш миниатюр", ex);
        }
    }

    private static string Key(string clip)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(clip.ToLowerInvariant()));
        return Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    private static ImageSource? Open(string file)
    {
        try
        {
            return File.Exists(file) ? Decode(File.ReadAllBytes(file)) : null;
        }
        catch (Exception ex)
        {
            Log.Error($"миниатюра не прочиталась: {file}", ex);
            return null;
        }
    }

    private static BitmapSource Shrink(RawFrame frame)
    {
        var source = BitmapSource.Create(
            (int)frame.Width,
            (int)frame.Height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            frame.Pixels,
            (int)frame.Width * 4);

        source.Freeze();

        var scale = Math.Min(BoxWidth / (double)frame.Width, BoxHeight / (double)frame.Height);

        if (scale >= 1)
        {
            return source;
        }

        var small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        small.Freeze();

        return small;
    }

    private static byte[] Encode(BitmapSource picture)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Quality };
        encoder.Frames.Add(BitmapFrame.Create(picture));

        using var memory = new MemoryStream();
        encoder.Save(memory);

        return memory.ToArray();
    }

    private static ImageSource Decode(byte[] data)
    {
        var picture = new BitmapImage();

        picture.BeginInit();
        picture.CacheOption = BitmapCacheOption.OnLoad;
        picture.StreamSource = new MemoryStream(data);
        picture.EndInit();
        picture.Freeze();

        return picture;
    }

    private static void Write(string file, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Root);

            var temporary = file + ".tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, file, true);
        }
        catch (Exception ex)
        {
            Log.Error($"миниатюра не записалась: {file}", ex);
        }
    }
}
