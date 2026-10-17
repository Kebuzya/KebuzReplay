using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kebuz.Recorder.Ipc;

public sealed record PipeMessage(string Type, JsonElement? Payload = null);

public static class PipeMessages
{
    public const string Ready = "Ready";
    public const string Shutdown = "Shutdown";
    public const string UpdateSettings = "UpdateSettings";
    public const string RecorderRestarted = "RecorderRestarted";
    public const string CaptureRecovered = "CaptureRecovered";
    public const string MarkClip = "MarkClip";
    public const string ClipSaved = "ClipSaved";
    public const string RecordingStateChanged = "RecordingStateChanged";
    public const string BufferMemoryUsage = "BufferMemoryUsage";
    public const string AudioSourceChanged = "AudioSourceChanged";
    public const string HotkeyChanged = "HotkeyChanged";
    public const string WatchAudioLevels = "WatchAudioLevels";
    public const string AudioLevels = "AudioLevels";
    public const string ApplySettingsNow = "ApplySettingsNow";
    public const string GameDetected = "GameDetected";
    public const string CaptureShown = "CaptureShown";
    public const string ShowWindow = "ShowWindow";
}

public static class PipeProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static System.Text.Encoding Encoding { get; } = new UTF8Encoding(false);

    public static PipeMessage Carry<T>(string type, T payload)
    {
        return new PipeMessage(type, JsonSerializer.SerializeToElement(payload, Options));
    }

    public static T? Unpack<T>(PipeMessage message)
    {
        if (message.Payload is not { } element)
        {
            return default;
        }

        try
        {
            return element.Deserialize<T>(Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static string Write(PipeMessage message)
    {
        return JsonSerializer.Serialize(message, Options);
    }

    public static PipeMessage? Read(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PipeMessage>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
