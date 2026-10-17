namespace Kebuz.Recorder.Ipc;

public sealed record RestartInfo(string Reason);

public sealed record RecoveryInfo(string Reason);

public sealed record ClipInfo(
    string Path,
    double Seconds,
    long SizeBytes,
    string SessionFolder,
    string Game,
    bool WithoutMicrophone = false);

public sealed record SourceHealth(string Kind, string Title, bool Live, string? Complaint);

public sealed record HotkeyState(string Combination, bool Registered, string? Complaint);

public sealed record RecordingState(
    bool Recording,
    string? Game,
    string? SessionFolder,
    bool Profiled = false,
    double BufferSeconds = 0,
    string? Hotkey = null,
    string? Capture = null,
    bool SettingsPending = false)
{
    public const string BorderlessNote = "Capture.Borderless";
    public const string BorderlessFailedNote = "Capture.BorderlessFailed";
    public const string BorderlessNotRaisedNote = "Capture.BorderlessNotRaised";
    public const string BlackoutNote = "Capture.Blackout";

    public bool LostBorderless()
    {
        return Recording && Localization.Strings.KeyOf(Capture) is BorderlessFailedNote or BorderlessNotRaisedNote;
    }
}

public sealed record BufferUsage(long Bytes);

public sealed record LevelWatch(bool On);

public sealed record SourceLevel(string Kind, string Title, float Peak, float Rms);

public sealed record AudioLevels(
    IReadOnlyList<SourceLevel> Sources,
    float MixPeak,
    float MixRms,
    bool Limited,
    bool Preview);

public sealed record DetectedGame(string Executable, string Path, string Title);

public sealed record CaptureShot(
    string Game,
    string Window,
    uint Width,
    uint Height,
    byte[] Png,
    DateTime Taken);
