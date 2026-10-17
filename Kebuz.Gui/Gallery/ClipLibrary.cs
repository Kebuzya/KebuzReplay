using System.Globalization;
using System.IO;
using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Gui.Gallery;

public sealed record ClipEntry(
    string Path,
    string Game,
    string Session,
    DateTime When,
    TimeSpan Duration,
    long SizeBytes,
    bool WithoutMicrophone = false);

internal static class ClipLibrary
{
    public static IReadOnlyList<ClipEntry> Scan(string root)
    {
        var found = new List<ClipEntry>();

        if (!Directory.Exists(root))
        {
            Log.Report($"папки клипов ещё нет: {root}");
            return found;
        }

        try
        {
            foreach (var game in Directory.EnumerateDirectories(root))
            {
                foreach (var session in Directory.EnumerateDirectories(game, "Session_*"))
                {
                    foreach (var clip in Directory.EnumerateFiles(session, "Clip_*.mp4"))
                    {
                        found.Add(Describe(clip, Path.GetFileName(game), Path.GetFileName(session)));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось перечитать {root}", ex);
        }

        found.Sort((left, right) => right.When.CompareTo(left.When));

        Log.Report($"клипов на диске: {found.Count} в {root}");

        return found;
    }

    public static ClipEntry Describe(string path, string game, string session)
    {
        var info = new FileInfo(path);

        return new ClipEntry(
            path,
            game,
            session,
            Stamp(Path.GetFileNameWithoutExtension(path), info),
            Mp4Duration.Read(path),
            info.Exists ? info.Length : 0);
    }

    public static DateTime StampOf(string path)
    {
        return Stamp(Path.GetFileNameWithoutExtension(path), new FileInfo(path));
    }

    public static DateTime SessionStamp(string folder, DateTime fallback)
    {
        return Parse(folder, out var parsed) ? parsed : fallback;
    }

    private static DateTime Stamp(string name, FileInfo info)
    {
        if (Parse(name, out var parsed))
        {
            return parsed;
        }

        return info.Exists ? info.LastWriteTime : DateTime.Now;
    }

    private static bool Parse(string name, out DateTime stamp)
    {
        var parts = name.Split('_');

        if (parts.Length >= 3)
        {
            return DateTime.TryParseExact(
                $"{parts[1]}_{parts[2]}",
                "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out stamp);
        }

        stamp = default;
        return false;
    }
}
