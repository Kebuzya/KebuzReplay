using System.ComponentModel;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Shell;

public sealed class UpdateModel : INotifyPropertyChanged
{
    private ReleaseInfo? latest;
    private bool enabled = true;
    private string? skipped;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? Dismissed;

    public UpdateModel()
    {
        Loc.Track(this, static owner => ((UpdateModel)owner).Relocalize());
    }

    public bool IsShown => enabled
        && latest is { } found
        && UpdateCheck.IsNewer(found.Version, UpdateCheck.Current)
        && !string.Equals(found.Version.ToString(3), skipped, StringComparison.Ordinal);

    public string Text => latest is null ? string.Empty : Strings.Format("Update.Available", latest.Version.ToString(3));

    public string Url => latest?.Url ?? $"{UpdateCheck.Page}/releases";

    public string? Version => latest?.Version.ToString(3);

    public void Adopt(bool checkUpdates, string? skippedVersion)
    {
        enabled = checkUpdates;
        skipped = skippedVersion;
        Raise();
    }

    public void Found(ReleaseInfo? release)
    {
        latest = release;
        Raise();
    }

    public void Dismiss()
    {
        if (latest is null)
        {
            return;
        }

        skipped = latest.Version.ToString(3);
        Raise();
        Dismissed?.Invoke(skipped);
    }

    private void Relocalize()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Url)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }
}
