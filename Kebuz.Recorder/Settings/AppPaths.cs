namespace Kebuz.Recorder.Settings;

public static class AppPaths
{
    public const string MarkerName = "kebuz.portable";

    private const string Company = "Kebuz";
    private const string Product = "KebuzReplay";

    private static readonly string[] Owned = ["kebuz-settings.json", "kebuz-apps.json", "kebuz-ignored.json"];

    public static bool Portable => File.Exists(Path.Combine(AppContext.BaseDirectory, MarkerName));

    public static string Settings => Ensure(Portable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Company, Product));

    public static string Local => Ensure(Portable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Company, Product));

    public static IReadOnlyList<string> AdoptOldFiles()
    {
        if (Portable)
        {
            return [];
        }

        var adopted = new List<string>();
        var target = Settings;

        foreach (var name in Owned)
        {
            var old = Path.Combine(AppContext.BaseDirectory, name);
            var fresh = Path.Combine(target, name);

            if (!File.Exists(old) || File.Exists(fresh))
            {
                continue;
            }

            File.Copy(old, fresh);
            adopted.Add(name);
        }

        return adopted;
    }

    private static string Ensure(string folder)
    {
        Directory.CreateDirectory(folder);
        return folder;
    }
}
