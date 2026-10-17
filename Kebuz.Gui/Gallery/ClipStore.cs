using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kebuz.Recorder.Diagnostics;

namespace Kebuz.Gui.Gallery;

public sealed record TagRecord(string Id, string Name, string Icon, string Color)
{
    public const string DefaultColor = "#00C3CB";

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Icon) ? Name : $"{Icon} {Name}";

    public TagRecord Fix()
    {
        return this with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Тег" : Name,
            Icon = Icon ?? string.Empty,
            Color = string.IsNullOrWhiteSpace(Color) ? DefaultColor : Color
        };
    }

    public override string ToString()
    {
        return Label;
    }
}

public sealed record ClipRecord(string Path, string[] Tags);

public sealed record ClipIndex(string Root, TagRecord[] Tags, ClipRecord[] Clips, string[]? Collapsed = null);

public sealed class ClipStore
{
    private const string FileName = "kebuz-clips.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] None = [];

    private readonly Dictionary<string, string[]> clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TagRecord> tags = [];
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;

    private ClipStore(string root)
    {
        this.root = root;
    }

    public static string BackupPath => Path.Combine(Kebuz.Recorder.Settings.AppPaths.Local, FileName);

    public IReadOnlyList<TagRecord> Tags => tags;

    public static ClipStore Load(string root)
    {
        var store = new ClipStore(root);
        var primary = Path.Combine(root, FileName);
        var index = Read(primary);

        if (index is null)
        {
            index = Read(BackupPath);

            if (index is not null && !Same(index.Root, root))
            {
                Log.Report($"запасной индекс клипов относится к {index.Root}, для {root} не годится");
                index = null;
            }
            else if (index is not null)
            {
                Log.Report($"индекс клипов взят из запасной копии: {BackupPath}");
            }
        }

        if (index is null)
        {
            return store;
        }

        foreach (var tag in index.Tags)
        {
            if (!string.IsNullOrWhiteSpace(tag.Id) && !store.Has(tag.Id))
            {
                store.tags.Add(tag.Fix());
            }
        }

        foreach (var clip in index.Clips)
        {
            if (!string.IsNullOrWhiteSpace(clip.Path))
            {
                store.clips[clip.Path] = clip.Tags ?? None;
            }
        }

        foreach (var game in index.Collapsed ?? [])
        {
            if (!string.IsNullOrWhiteSpace(game))
            {
                store.collapsed.Add(game);
            }
        }

        return store;
    }

    public IReadOnlyList<TagRecord> TagsOf(string path)
    {
        if (!clips.TryGetValue(Key(path), out var assigned) || assigned.Length == 0)
        {
            return [];
        }

        var found = new List<TagRecord>(assigned.Length);

        foreach (var id in assigned)
        {
            var tag = Find(id);

            if (tag is not null)
            {
                found.Add(tag);
            }
        }

        return found;
    }

    public bool IsMarked(string path)
    {
        return TagsOf(path).Count > 0;
    }

    public void Assign(string path, IReadOnlyList<string> ids)
    {
        var key = Key(path);

        if (ids.Count == 0)
        {
            clips.Remove(key);
        }
        else
        {
            clips[key] = [.. ids];
        }
    }

    public TagRecord Define(string name, string icon, string color)
    {
        var tag = new TagRecord(Guid.NewGuid().ToString("N"), name, icon, color).Fix();
        tags.Add(tag);
        return tag;
    }

    public TagRecord? Redefine(string id, string name, string icon, string color)
    {
        var index = tags.FindIndex(tag => string.Equals(tag.Id, id, StringComparison.Ordinal));

        if (index < 0)
        {
            return null;
        }

        var tag = new TagRecord(id, name, icon, color).Fix();
        tags[index] = tag;

        return tag;
    }

    public void Undefine(string id)
    {
        tags.RemoveAll(tag => string.Equals(tag.Id, id, StringComparison.Ordinal));

        foreach (var key in clips.Keys.ToList())
        {
            var assigned = clips[key];

            if (!assigned.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            var left = assigned.Where(other => !string.Equals(other, id, StringComparison.Ordinal)).ToArray();

            if (left.Length == 0)
            {
                clips.Remove(key);
            }
            else
            {
                clips[key] = left;
            }
        }
    }

    public bool IsCollapsed(string game)
    {
        return collapsed.Contains(game);
    }

    public void Collapse(string game, bool folded)
    {
        if (folded)
        {
            collapsed.Add(game);
        }
        else
        {
            collapsed.Remove(game);
        }
    }

    public void Forget(string path)
    {
        clips.Remove(Key(path));
    }

    public bool Prune(IReadOnlyCollection<string> alive)
    {
        if (clips.Count == 0)
        {
            return false;
        }

        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in alive)
        {
            kept.Add(Key(path));
        }

        var gone = clips.Keys.Where(key => !kept.Contains(key)).ToList();

        foreach (var key in gone)
        {
            clips.Remove(key);
        }

        if (gone.Count > 0)
        {
            Log.Report($"из индекса клипов убрано записей об исчезнувших файлах: {gone.Count}");
        }

        return gone.Count > 0;
    }

    public void Save()
    {
        var index = new ClipIndex(
            root,
            [.. tags],
            [.. clips.Select(pair => new ClipRecord(pair.Key, pair.Value))],
            [.. collapsed.Order(StringComparer.OrdinalIgnoreCase)]);

        var text = JsonSerializer.Serialize(index, Format);

        Write(Path.Combine(root, FileName), text);
        Write(BackupPath, text);
    }

    private static ClipIndex? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var index = JsonSerializer.Deserialize<ClipIndex>(File.ReadAllText(path), Format);

            if (index is null)
            {
                return null;
            }

            return index with
            {
                Root = index.Root ?? string.Empty,
                Tags = index.Tags ?? [],
                Clips = index.Clips ?? [],
                Collapsed = index.Collapsed ?? []
            };
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось прочитать индекс клипов {path}", ex);
            return null;
        }
    }

    private static void Write(string path, string text)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, true);
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось записать индекс клипов {path}", ex);
        }
    }

    private static bool Same(string left, string right)
    {
        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool Has(string id)
    {
        return Find(id) is not null;
    }

    private TagRecord? Find(string id)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Id, id, StringComparison.Ordinal))
            {
                return tag;
            }
        }

        return null;
    }

    private string Key(string path)
    {
        return Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
    }
}
