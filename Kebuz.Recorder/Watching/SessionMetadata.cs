using System.Text.Json;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Settings;

namespace Kebuz.Recorder.Watching;

public sealed record SessionMetadata(CaptureSource Source, DateTime Started, RecordingMethod Method,
    uint? Width = null, uint? Height = null, uint? FrameRate = null)
{
    public static SessionMetadata? Read(string? folder)
    {
        if (folder is null) return null;
        var path = Path.Combine(folder, "session.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
