namespace Kebuz.Recorder.Watching;

public static class SessionPaths
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "KebuzReplay");

    public static string SessionFolder(string root, string title, DateTime started)
    {
        return Path.Combine(root, Sanitize(title), $"Session_{started:yyyy-MM-dd_HH-mm-ss}");
    }

    public static string ClipPath(string folder, DateTime saved)
    {
        var name = $"Clip_{saved:yyyy-MM-dd_HH-mm-ss}";
        var path = Path.Combine(folder, $"{name}.mp4");

        for (var index = 2; File.Exists(path); index++)
        {
            path = Path.Combine(folder, $"{name}_{index}.mp4");
        }

        return path;
    }

    private static string Sanitize(string title)
    {
        var cleaned = string.Join('_', title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.');

        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }
}
