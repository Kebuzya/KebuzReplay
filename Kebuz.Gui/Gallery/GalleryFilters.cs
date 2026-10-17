using System.ComponentModel;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Localization;

namespace Kebuz.Gui.Gallery;

public sealed class PickOption : INotifyPropertyChanged
{
    private bool picked;

    public PickOption(string value)
    {
        Value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value { get; }

    public bool IsPicked
    {
        get => picked;
        set
        {
            if (picked == value)
            {
                return;
            }

            picked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPicked)));
        }
    }

    public override string ToString()
    {
        return Value;
    }
}

public sealed class FilterOption : INotifyPropertyChanged
{
    private readonly string source;
    private readonly bool keyed;

    public FilterOption(string key, string label, bool keyed = false)
    {
        Key = key;
        source = label;
        this.keyed = keyed;

        if (keyed)
        {
            Loc.Track(this, static owner => ((FilterOption)owner).Relocalize());
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Label => keyed ? Strings.Get(source) : source;

    public bool IsAll => Key.Length == 0;

    public override string ToString()
    {
        return Label;
    }

    private void Relocalize()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public static class GalleryFilters
{
    public const string Untagged = "!";

    public static readonly FilterOption AnyGame = new(string.Empty, "Filter.Game", keyed: true);

    public static readonly FilterOption AnyTag = new(string.Empty, "Filter.Tag", keyed: true);

    public static readonly FilterOption NoTags = new(Untagged, "Filter.NoTags", keyed: true);

    public static readonly FilterOption[] Dates =
    [
        new(string.Empty, "Filter.Date", keyed: true),
        new("today", "Filter.Today", keyed: true),
        new("week", "Filter.Week", keyed: true),
        new("month", "Filter.Month", keyed: true)
    ];

    public static readonly FilterOption[] Orders =
    [
        new("new", "Filter.NewFirst", keyed: true),
        new("old", "Filter.OldFirst", keyed: true),
        new("long", "Filter.LongFirst", keyed: true),
        new("big", "Filter.BigFirst", keyed: true)
    ];

    public static DateTime Since(string key)
    {
        return key switch
        {
            "today" => DateTime.Today,
            "week" => DateTime.Today.AddDays(-6),
            "month" => DateTime.Today.AddDays(-29),
            _ => DateTime.MinValue
        };
    }
}
