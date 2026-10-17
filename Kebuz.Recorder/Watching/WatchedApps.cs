using System.Text.Json;
using System.Text.Json.Serialization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Recorder.Watching;

public sealed record WatchedApp(string Executable, string Title, string? Path, AppProfile? Profile = null)
{
    [JsonIgnore]
    public AppProfile Overrides => Profile ?? AppProfile.Empty;

    public override string ToString()
    {
        return $"{Title}, {Executable}";
    }
}

public sealed class WatchedApps
{
    internal static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly List<WatchedApp> items;

    private WatchedApps(string path, List<WatchedApp> items)
    {
        StoragePath = path;
        this.items = items;
    }

    public string StoragePath { get; }

    public IReadOnlyList<WatchedApp> Items => items;

    public static string DefaultPath => Path.Combine(Settings.AppPaths.Settings, "kebuz-apps.json");

    public static WatchedApps Load(string? path = null)
    {
        var full = Path.GetFullPath(path ?? DefaultPath);

        if (!File.Exists(full))
        {
            return new WatchedApps(full, []);
        }

        var stored = JsonSerializer.Deserialize<StoredApps>(File.ReadAllText(full), Format);
        var items = stored?.Watched ?? [];

        return new WatchedApps(full, [.. items.Where(item => !string.IsNullOrWhiteSpace(item.Executable))]);
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(StoragePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(StoragePath, JsonSerializer.Serialize(new StoredApps(items), Format));
    }

    public WatchedApp Add(string target, string? title = null)
    {
        var executable = Executable(target);

        var path = target.Contains(Path.DirectorySeparatorChar) || target.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(target)
            : null;

        var known = Find(executable, path);

        var app = new WatchedApp(
            executable,
            string.IsNullOrWhiteSpace(title) ? known?.Title ?? Path.GetFileNameWithoutExtension(executable) : title.Trim(),
            path,
            known?.Profile);

        items.RemoveAll(item => Is(item, executable, path));
        items.Add(app);

        return app;
    }

    public static string TitleOf(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);

            foreach (var name in new[] { info.ProductName, info.FileDescription })
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
        }
        catch (Exception)
        {
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    public WatchedApp? Find(string executable, string? path)
    {
        return items.Find(item => Is(item, executable, path));
    }

    public bool SetProfile(WatchedApp app, AppProfile? profile)
    {
        var index = items.FindIndex(item => Is(item, app.Executable, app.Path));

        if (index < 0)
        {
            return false;
        }

        items[index] = items[index] with { Profile = profile is { Any: true } ? profile : null };

        return true;
    }

    public bool Remove(WatchedApp app)
    {
        return items.RemoveAll(item => Is(item, app.Executable, app.Path)) > 0;
    }

    public bool Remove(string target)
    {
        var executable = Executable(target);
        return items.RemoveAll(item => Same(item.Executable, executable)) > 0;
    }

    private static bool Is(WatchedApp item, string executable, string? path)
    {
        return Same(item.Executable, executable)
            && (item.Path is null ? path is null : Same(item.Path, path));
    }

    internal static string Executable(string target)
    {
        var name = Path.GetFileName(target);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"Не похоже на имя или путь к exe: {target}");
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public WatchedApp? Match(string executable, Func<string?> resolvePath)
    {
        WatchedApp? loose = null;
        WatchedApp? first = null;
        var pathed = false;

        foreach (var item in items)
        {
            if (!Same(item.Executable, executable))
            {
                continue;
            }

            first ??= item;

            if (item.Path is null)
            {
                loose ??= item;
            }
            else
            {
                pathed = true;
            }
        }

        if (first is null || !pathed)
        {
            return first;
        }

        var path = resolvePath();

        if (path is null)
        {
            return loose ?? first;
        }

        return items.Find(item => Same(item.Executable, executable) && Same(item.Path, path)) ?? loose;
    }

    internal static bool Same(string? left, string? right)
    {
        return left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StoredApps(List<WatchedApp> Watched);
}
