namespace Kebuz.Recorder.Ipc;

public enum CaptureSourceKind
{
    Window,
    Monitor
}

public sealed record CaptureSource(string Id, CaptureSourceKind Kind, string Title, string Application,
    string? ExecutablePath, uint ProcessId, long Handle, int Width, int Height, bool Primary = false);

public sealed record CaptureSourceList(IReadOnlyList<CaptureSource> Sources, string? Error = null);

public sealed record RecordingCommand(string Action, bool SaveBuffer = false, string? RecoveryId = null);

public sealed record RecordingControlState(bool Active, bool Manual, bool Continuous, string Stage,
    CaptureSource? Source = null, double Seconds = 0, long Bytes = 0, string? Error = null,
    bool StopConfirmation = false, IReadOnlyList<RecoveryFile>? Recoveries = null);

public sealed record RecoveryFile(string Id, string Folder, string Group, string Started)
{
    public override string ToString() => Folder;
}
