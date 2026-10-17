using System.Text.Json.Serialization;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Input;

namespace Kebuz.Recorder.Settings;

public sealed record QualityOverride(
    QualityPreset Preset,
    uint Height,
    uint FrameRate,
    uint BitrateKbps,
    CaptureMode Capture = CaptureMode.Standard);

public sealed record BufferOverride(double BufferSeconds, double MergeSeconds, double MergeMaxSeconds);

public sealed record AppProfile(
    QualityOverride? Quality = null,
    BufferOverride? Buffer = null,
    AudioSettings? Audio = null,
    string? Hotkey = null)
{
    public static AppProfile Empty { get; } = new();

    [JsonIgnore]
    public bool Any => Quality is not null || Buffer is not null || Audio is not null || Hotkey is not null;

    public KebuzSettings Apply(KebuzSettings common)
    {
        var merged = common;

        if (Quality is not null)
        {
            merged = merged with
            {
                Quality = Quality.Preset,
                CustomHeight = Quality.Height,
                CustomFrameRate = Quality.FrameRate,
                CustomBitrateKbps = Quality.BitrateKbps,
                Capture = Quality.Capture
            };
        }

        if (Buffer is not null)
        {
            merged = merged with
            {
                BufferSeconds = Buffer.BufferSeconds,
                MergeSeconds = Buffer.MergeSeconds,
                MergeMaxSeconds = Buffer.MergeMaxSeconds
            };
        }

        if (Audio is not null)
        {
            merged = merged with { Audio = Audio };
        }

        if (Hotkey is not null && HotkeyRules.IsValid(Hotkey))
        {
            merged = merged with { Hotkey = Hotkey };
        }

        return ReferenceEquals(merged, common) ? common : SettingsStore.Fix(merged);
    }

    public override string ToString()
    {
        if (!Any)
        {
            return Localization.Strings.Get("Profile.AsCommon");
        }

        var parts = new List<string>();

        if (Quality is not null)
        {
            parts.Add(
                Localization.Strings.Format("Profile.Quality", QualityPresets.Resolve(Quality.Preset, Quality.Height, Quality.FrameRate, Quality.BitrateKbps).Title) +
                (Quality.Capture is CaptureMode.Borderless ? Localization.Strings.Get("Profile.Borderless") : string.Empty));
        }

        if (Buffer is not null)
        {
            parts.Add(Localization.Strings.Format("Profile.Buffer", Buffer.BufferSeconds));
        }

        if (Audio is not null)
        {
            parts.Add(Localization.Strings.Format("Profile.Sound", Audio));
        }

        if (Hotkey is not null)
        {
            parts.Add(Localization.Strings.Format("Profile.Hotkey", Hotkey));
        }

        return string.Join(", ", parts);
    }
}
