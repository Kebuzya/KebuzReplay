using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kebuz.Recorder.Watching;

public sealed record IgnoredApp(string Executable, string? Path)
{
    [JsonIgnore]
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Executable);

    public override string ToString()
    {
        return Path ?? Executable;
    }
}

public sealed class IgnoredApps
{
    private readonly List<IgnoredApp> items;

    private IgnoredApps(string path, List<IgnoredApp> items)
    {
        StoragePath = path;
        this.items = items;
    }

    public string StoragePath { get; }

    public IReadOnlyList<IgnoredApp> Items => items;

    public static string DefaultPath => Path.Combine(Settings.AppPaths.Settings, "kebuz-ignored.json");

    public static IgnoredApps Empty => new(Path.GetFullPath(DefaultPath), []);

    public static IgnoredApps Load(string? path = null)
    {
        var full = Path.GetFullPath(path ?? DefaultPath);

        if (!File.Exists(full))
        {
            return new IgnoredApps(full, []);
        }

        var stored = JsonSerializer.Deserialize<StoredIgnored>(File.ReadAllText(full), WatchedApps.Format);
        var items = stored?.Ignored ?? [];

        return new IgnoredApps(full, [.. items.Where(item => !string.IsNullOrWhiteSpace(item.Executable))]);
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(StoragePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(StoragePath, JsonSerializer.Serialize(new StoredIgnored(items), WatchedApps.Format));
    }

    public IgnoredApp Add(string target)
    {
        var executable = WatchedApps.Executable(target);

        var path = target.Contains(Path.DirectorySeparatorChar) || target.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(target)
            : null;

        var app = new IgnoredApp(executable, path);

        items.RemoveAll(item => Is(item, executable, path));
        items.Add(app);

        return app;
    }

    public bool Remove(IgnoredApp app)
    {
        return items.RemoveAll(item => Is(item, app.Executable, app.Path)) > 0;
    }

    public bool Contains(string executable, string? path)
    {
        foreach (var item in items)
        {
            if (WatchedApps.Same(item.Executable, executable)
                && (item.Path is null || WatchedApps.Same(item.Path, path)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Is(IgnoredApp item, string executable, string? path)
    {
        return WatchedApps.Same(item.Executable, executable)
            && (item.Path is null ? path is null : WatchedApps.Same(item.Path, path));
    }

    private sealed record StoredIgnored(List<IgnoredApp> Ignored);
}
