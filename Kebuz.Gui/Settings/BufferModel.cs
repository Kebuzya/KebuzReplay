using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Settings;

public sealed class BufferModel : INotifyPropertyChanged, ISection
{
    private readonly Func<KebuzSettings> effective;
    private readonly Action touched;
    private readonly Action commit;

    private double bufferSeconds = KebuzSettings.Default.BufferSeconds;
    private double mergeSeconds = KebuzSettings.Default.MergeSeconds;
    private double mergeMaxSeconds = KebuzSettings.Default.MergeMaxSeconds;

    public BufferModel(Func<KebuzSettings> effective, Action touched, Action commit)
    {
        this.effective = effective;
        this.touched = touched;
        this.commit = commit;
        Loc.Track(this, static owner => ((BufferModel)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BufferOverride Override => new(bufferSeconds, mergeSeconds, mergeMaxSeconds);

    public double Seconds => bufferSeconds;

    public double BufferSeconds
    {
        get => bufferSeconds;
        set
        {
            if (Math.Abs(bufferSeconds - value) < 0.5)
            {
                return;
            }

            bufferSeconds = value;
            Raise(nameof(BufferSeconds));
            Raise(nameof(BufferText));
            touched();
        }
    }

    public double MergeSeconds
    {
        get => mergeSeconds;
        set
        {
            if (Math.Abs(mergeSeconds - value) < 0.5)
            {
                return;
            }

            mergeSeconds = value;
            Raise(nameof(MergeSeconds));
            Raise(nameof(MergeText));

            if (mergeMaxSeconds < mergeSeconds)
            {
                mergeMaxSeconds = mergeSeconds;
                Raise(nameof(MergeMaxSeconds));
                Raise(nameof(MergeMaxText));
            }

            touched();
        }
    }

    public double MergeMaxSeconds
    {
        get => mergeMaxSeconds;
        set
        {
            if (Math.Abs(mergeMaxSeconds - value) < 0.5)
            {
                return;
            }

            mergeMaxSeconds = value;
            Raise(nameof(MergeMaxSeconds));
            Raise(nameof(MergeMaxText));

            if (mergeSeconds > mergeMaxSeconds)
            {
                mergeSeconds = mergeMaxSeconds;
                Raise(nameof(MergeSeconds));
                Raise(nameof(MergeText));
            }

            touched();
        }
    }

    public string BufferText => Sizes.Length(TimeSpan.FromSeconds(bufferSeconds));

    public string MergeText => Strings.Format("Settings.Buffer.Seconds", mergeSeconds);

    public string MergeMaxText => Strings.Format("Settings.Buffer.Seconds", mergeMaxSeconds);

    public string MemoryText =>
        Strings.Format("Settings.Buffer.Memory", Sizes.Format(Bytes()));

    public void Adopt(double buffer, double merge, double mergeMax)
    {
        bufferSeconds = buffer;
        mergeSeconds = merge;
        mergeMaxSeconds = mergeMax;

        Raise(nameof(BufferSeconds));
        Raise(nameof(MergeSeconds));
        Raise(nameof(MergeMaxSeconds));
        Raise(nameof(BufferText));
        Raise(nameof(MergeText));
        Raise(nameof(MergeMaxText));
        Refresh();
    }

    public void Refresh()
    {
        Raise(nameof(MemoryText));
    }

    public void Commit()
    {
        commit();
    }

    public long Bytes()
    {
        var settings = effective();

        return Formats.BufferBytes(
            settings.Profile,
            settings.BufferSeconds,
            settings.MergeMaxSeconds,
            settings.Audio);
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
