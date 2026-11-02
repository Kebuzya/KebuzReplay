using Kebuz.Recorder.Encoding;

namespace Kebuz.Recorder.Settings;

public enum RecordingMethod
{
    Buffer,
    Continuous
}

public sealed record ManualRecordingSettings(
    QualityPreset Quality,
    uint Height,
    uint FrameRate,
    uint BitrateKbps,
    double BufferSeconds,
    double MergeSeconds,
    double MergeMaxSeconds,
    AudioSettings Audio,
    RecordingMethod Method = RecordingMethod.Buffer,
    string? SourceId = null)
{
    public static ManualRecordingSettings From(KebuzSettings common) => new(
        common.Quality, common.CustomHeight, common.CustomFrameRate, common.CustomBitrateKbps,
        common.BufferSeconds, common.MergeSeconds, common.MergeMaxSeconds,
        (common.Audio ?? AudioSettings.Default) with { Mode = SystemAudioMode.Desktop });

    public KebuzSettings Apply(KebuzSettings common) => common with
    {
        Quality = Quality,
        CustomHeight = Height,
        CustomFrameRate = FrameRate,
        CustomBitrateKbps = BitrateKbps,
        BufferSeconds = BufferSeconds,
        MergeSeconds = MergeSeconds,
        MergeMaxSeconds = MergeMaxSeconds,
        Audio = Audio,
        Capture = CaptureMode.Standard
    };
}
