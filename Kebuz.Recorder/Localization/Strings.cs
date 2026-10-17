using System.Globalization;
using System.Text.Json;

namespace Kebuz.Recorder.Localization;

public enum Language
{
    Russian,
    English
}

public static class Strings
{
    public const string System = "system";
    public const string RussianCode = "ru";
    public const string EnglishCode = "en";

    private const char Separator = '\u001F';

    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly IReadOnlyDictionary<string, string> RussianTable = Load(RussianCode);
    private static readonly IReadOnlyDictionary<string, string> EnglishTable = Load(EnglishCode);

    private static volatile Language current = Language.Russian;

    public static event Action? Changed;

    public static Language Current => current;

    public static CultureInfo Culture => current == Language.English ? EnglishCulture : RussianCulture;

    public static Language Resolve(string? setting)
    {
        return setting switch
        {
            RussianCode => Language.Russian,
            EnglishCode => Language.English,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == RussianCode ? Language.Russian : Language.English
        };
    }

    public static void Use(Language next)
    {
        if (next == current)
        {
            return;
        }

        current = next;
        Changed?.Invoke();
    }

    public static string Get(string key)
    {
        var table = current == Language.English ? EnglishTable : RussianTable;

        if (table.TryGetValue(key, out var value) || RussianTable.TryGetValue(key, out value))
        {
            return value;
        }

        return key;
    }

    public static string Format(string key, params object?[] args)
    {
        return string.Format(Culture, Get(key), args);
    }

    public static string Plural(long count, string key)
    {
        return Get($"{key}.{Form(count)}");
    }

    public static bool Has(string key)
    {
        return RussianTable.ContainsKey(key);
    }

    public static string Code(string key, params string[] args)
    {
        return args.Length == 0 ? key : string.Join(Separator, [key, .. args]);
    }

    public static string KeyOf(string? coded)
    {
        return coded is null ? string.Empty : coded.Split(Separator)[0];
    }

    public static string Render(string? coded)
    {
        if (string.IsNullOrEmpty(coded))
        {
            return string.Empty;
        }

        var parts = coded.Split(Separator);

        if (!RussianTable.ContainsKey(parts[0]))
        {
            return coded;
        }

        return parts.Length == 1
            ? Get(parts[0])
            : Format(parts[0], [.. parts[1..].Select(arg => (object?)(RussianTable.ContainsKey(arg) ? Get(arg) : arg))]);
    }

    public static IReadOnlyDictionary<string, string> Table(Language language)
    {
        return language == Language.English ? EnglishTable : RussianTable;
    }

    private static string Form(long count)
    {
        if (current == Language.English)
        {
            return count == 1 ? "One" : "Many";
        }

        var tail = Math.Abs(count) % 100;

        if (tail is >= 11 and <= 14)
        {
            return "Many";
        }

        return (tail % 10) switch
        {
            1 => "One",
            2 or 3 or 4 => "Few",
            _ => "Many"
        };
    }

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream($"Kebuz.Text.{code}.json");

        if (stream is null)
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
