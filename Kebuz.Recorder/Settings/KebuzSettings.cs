using System.Text.Json;
using System.Text.Json.Serialization;
using Kebuz.Recorder.Audio;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Input;
using Kebuz.Recorder.Watching;

namespace Kebuz.Recorder.Settings;

public sealed record KebuzSettings(
    QualityPreset Quality,
    double BufferSeconds,
    double MergeSeconds,
    double MergeMaxSeconds,
    string Hotkey,
    string? ClipsRoot,
    bool LimitFolder,
    double LimitGigabytes,
    bool DeleteOld,
    AudioSettings Audio,
    uint CustomHeight = 810,
    uint CustomFrameRate = 48,
    uint CustomBitrateKbps = 2500,
    CaptureMode Capture = CaptureMode.Standard,
    bool DetectGames = true,
    bool KeepAfterExit = true,
    double KeepAfterExitSeconds = 60,
    bool FollowLargerWindow = false,
    bool CheckUpdates = true,
    string? SkippedVersion = null,
    string UiLanguage = Localization.Strings.System)
{
    public static KebuzSettings Default { get; } = new(
        QualityPresets.Default,
        120,
        8,
        20,
        "Ctrl+Alt+R",
        null,
        false,
        20,
        false,
        AudioSettings.Default);

    [JsonIgnore]
    public string EffectiveClipsRoot => string.IsNullOrWhiteSpace(ClipsRoot)
        ? SessionPaths.DefaultRoot
        : ClipsRoot;

    [JsonIgnore]
    public QualityProfile Profile =>
        QualityPresets.Resolve(Quality, CustomHeight, CustomFrameRate, CustomBitrateKbps);

    public override string ToString()
    {
        return $"качество {Profile.Title}, буфер {BufferSeconds:F0} с, окно тишины {MergeSeconds:F0} с, " +
               $"потолок склейки {MergeMaxSeconds:F0} с, хоткей {Hotkey}, клипы {EffectiveClipsRoot}, " +
               $"хранение {Storage()}, звук {Audio}" +
               (Capture is CaptureMode.Borderless ? ", захват без рамки" : string.Empty) +
               (KeepAfterExit ? $", после выхода игры {KeepAfterExitSeconds:F0} с" : ", после выхода игры не пишет") +
               (FollowLargerWindow ? ", переход на большее окно" : string.Empty);
    }

    public KebuzSettings Recorded()
    {
        return this with
        {
            ClipsRoot = null,
            LimitFolder = false,
            LimitGigabytes = 0,
            DeleteOld = false,
            DetectGames = true,
            KeepAfterExit = true,
            KeepAfterExitSeconds = 60,
            FollowLargerWindow = false,
            CheckUpdates = true,
            SkippedVersion = null,
            UiLanguage = Localization.Strings.System
        };
    }

    private string Storage()
    {
        if (!LimitFolder)
        {
            return "без лимита";
        }

        return DeleteOld
            ? $"лимит {LimitGigabytes:F0} ГБ с автоудалением"
            : $"лимит {LimitGigabytes:F0} ГБ без автоудаления";
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string DefaultPath => Path.Combine(AppPaths.Settings, "kebuz-settings.json");

    public static KebuzSettings Load(string? path = null)
    {
        var full = Path.GetFullPath(path ?? DefaultPath);

        if (!File.Exists(full))
        {
            Save(KebuzSettings.Default, full);
            return KebuzSettings.Default;
        }

        var stored = JsonSerializer.Deserialize<KebuzSettings>(File.ReadAllText(full), Format);

        return stored is null ? KebuzSettings.Default : Fix(stored);
    }

    public static void Save(KebuzSettings settings, string? path = null)
    {
        var full = Path.GetFullPath(path ?? DefaultPath);
        var directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, JsonSerializer.Serialize(settings, Format));
    }

    public static KebuzSettings Fix(KebuzSettings settings)
    {
        var merge = settings.MergeSeconds > 0
            ? settings.MergeSeconds
            : KebuzSettings.Default.MergeSeconds;

        var mergeMax = settings.MergeMaxSeconds > 0
            ? settings.MergeMaxSeconds
            : KebuzSettings.Default.MergeMaxSeconds;

        return settings with
        {
            BufferSeconds = settings.BufferSeconds > 0
                ? settings.BufferSeconds
                : KebuzSettings.Default.BufferSeconds,
            MergeSeconds = merge,
            MergeMaxSeconds = Math.Max(merge, mergeMax),
            CustomHeight = Math.Clamp(settings.CustomHeight, QualityPresets.MinHeight, QualityPresets.MaxHeight),
            CustomFrameRate = Math.Clamp(
                settings.CustomFrameRate,
                QualityPresets.MinFrameRate,
                QualityPresets.MaxFrameRate),
            CustomBitrateKbps = Math.Clamp(
                settings.CustomBitrateKbps,
                QualityPresets.MinBitrateKbps,
                QualityPresets.MaxBitrateKbps),
            Hotkey = HotkeyRules.IsValid(settings.Hotkey)
                ? settings.Hotkey
                : KebuzSettings.Default.Hotkey,
            LimitGigabytes = settings.LimitGigabytes > 0
                ? settings.LimitGigabytes
                : KebuzSettings.Default.LimitGigabytes,
            DeleteOld = settings.LimitFolder && settings.DeleteOld,
            UiLanguage = settings.UiLanguage is Localization.Strings.RussianCode or Localization.Strings.EnglishCode
                ? settings.UiLanguage
                : Localization.Strings.System,
            Audio = (settings.Audio ?? AudioSettings.Default).Fix(AudioDevices.PerProcessSupported)
        };
    }
}
