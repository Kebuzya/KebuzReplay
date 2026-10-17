using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Kebuz.Gui.Shell;

public sealed record ReleaseInfo(Version Version, string Tag, string Url);

public static class UpdateCheck
{
    public const string Repository = "Kebuzya/KebuzReplay";
    public const string Page = "https://github.com/" + Repository;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static Version Current { get; } = Trim(typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentText => Current.ToString(3);

    public static async Task<ReleaseInfo?> Latest(string repository = Repository, CancellationToken cancel = default)
    {
        using var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KebuzReplay", CurrentText));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await client.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancel);

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    public static ReleaseInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } name)
        {
            return null;
        }

        if (ParseTag(name) is not { } version)
        {
            return null;
        }

        var url = root.TryGetProperty("html_url", out var page) && page.GetString() is { } link ? link : $"{Page}/releases";

        return new ReleaseInfo(version, name, url);
    }

    public static Version? ParseTag(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(['-', '+', ' ']);

        if (cut >= 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out var parsed) ? Trim(parsed) : null;
    }

    public static bool IsNewer(Version found, Version current)
    {
        return Trim(found) > Trim(current);
    }

    private static Version Trim(Version version)
    {
        return new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }
}
