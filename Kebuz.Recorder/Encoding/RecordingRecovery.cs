using System.Runtime.InteropServices;
using System.Text.Json;
using Kebuz.Recorder.Ipc;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

public sealed record RecordingInspection(long VideoSamples, long AudioSamples, double Seconds, long Bytes);

internal sealed record PendingRecording(string Id, string Group, DateTime Started, string OutputName);

public static class RecordingRecovery
{
    internal const string ManifestName = "recording.pending.json";
    internal const string SourceName = "recording.fragmented.mp4";
    private const uint AnyStream = 0xFFFFFFFE;
    private const int NoSuchStream = unchecked((int)0xC00D36B3);

    internal static void Begin(string folder, string group)
    {
        var now = DateTime.Now;
        var pending = new PendingRecording(Guid.NewGuid().ToString("N"), group, now,
            Path.GetFileName(Watching.SessionPaths.ClipPath(folder, now)));
        File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(pending));
    }

    public static IReadOnlyList<RecoveryFile> Find(string root)
    {
        var found = new List<RecoveryFile>();
        if (!Directory.Exists(root)) return found;
        foreach (var group in Directory.EnumerateDirectories(root))
        {
            foreach (var folder in Directory.EnumerateDirectories(group, "Session_*"))
            {
                var path = Path.Combine(folder, ManifestName);
                if (!File.Exists(path)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<PendingRecording>(File.ReadAllText(path));
                    if (item is not null && item.Id.Length == 32 && Path.GetFileName(item.OutputName) == item.OutputName)
                    {
                        found.Add(new RecoveryFile(item.Id, folder, item.Group, item.Started.ToString("O")));
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    Diagnostics.Log.Report($"не удалось прочитать незавершённую запись {path}: {ex.Message}");
                }
            }
        }
        return found;
    }

    public static ClipInfo Recover(string root, string id)
    {
        var item = Find(root).SingleOrDefault(file => file.Id == id)
            ?? throw new InvalidOperationException("Незавершённая запись не найдена");
        return Complete(item.Folder);
    }

    internal static ClipInfo Complete(string folder)
    {
        var manifest = Path.Combine(folder, ManifestName);
        var pending = JsonSerializer.Deserialize<PendingRecording>(File.ReadAllText(manifest))
            ?? throw new InvalidDataException("Нет описания записи");
        if (Path.GetFileName(pending.OutputName) != pending.OutputName)
            throw new InvalidDataException("Недопустимое имя записи");
        var source = Path.Combine(folder, SourceName);
        var temporary = Path.Combine(folder, "recording.partial.mp4");
        var target = Path.Combine(folder, pending.OutputName);
        if (!File.Exists(source) && File.Exists(target))
        {
            var complete = Inspect(target);
            if (complete.VideoSamples == 0) throw new InvalidDataException("Готовая запись не содержит кадров");
            File.Delete(manifest);
            return new ClipInfo(target, complete.Seconds, complete.Bytes, folder, pending.Group);
        }
        var expected = Inspect(source);
        if (expected.VideoSamples == 0) throw new InvalidDataException("Запись не содержит завершённых видеокадров");
        if (File.Exists(target))
        {
            Match(expected, Inspect(target));
        }
        else
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            Remux(source, temporary);
            Match(expected, Inspect(temporary));
            File.Move(temporary, target);
        }
        File.Delete(source);
        File.Delete(manifest);
        var info = new FileInfo(target);
        return new ClipInfo(target, expected.Seconds, info.Length, folder, pending.Group);
    }

    private static void Match(RecordingInspection expected, RecordingInspection actual)
    {
        if (expected.VideoSamples != actual.VideoSamples || expected.AudioSamples != actual.AudioSamples
            || Math.Abs(expected.Seconds - actual.Seconds) > 0.05)
            throw new InvalidDataException("Проверка готового MP4 не прошла, исходник сохранён");
    }

    public static void Remux(string source, string target)
    {
        if (Directory.Exists(target)) throw new IOException("Путь готового файла занят папкой, исходник сохранён");
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Исходный и готовый файл должны различаться");
        MediaFoundationRuntime.EnsureStarted();
        PInvoke.MFCreateSourceReaderFromURL(source, null, out var reader).ThrowOnFailure();
        var tracks = Tracks(reader);
        try
        {
            using var writer = new CompressedFileWriter(target, tracks.VideoType, tracks.AudioType, false);
            long? videoTime = null;
            Read(reader, tracks.Video, tracks.Audio, (isAudio, sample) =>
            {
                if (!isAudio)
                {
                    sample.GetSampleTime(out var time);
                    if (videoTime is { } previous && time <= previous)
                    {
                        time = previous + 1;
                        sample.SetSampleTime(time);
                    }
                    videoTime = time;
                }
                writer.Write(isAudio, sample);
            });
            writer.Finish();
        }
        finally
        {
            Marshal.ReleaseComObject(tracks.VideoType);
            if (tracks.AudioType is not null) Marshal.ReleaseComObject(tracks.AudioType);
            Marshal.ReleaseComObject(reader);
        }
    }

    public static RecordingInspection Inspect(string path)
    {
        MediaFoundationRuntime.EnsureStarted();
        PInvoke.MFCreateSourceReaderFromURL(path, null, out var reader).ThrowOnFailure();
        var tracks = Tracks(reader);
        long video = 0, audio = 0, end = 0;
        try
        {
            Read(reader, tracks.Video, tracks.Audio, (isAudio, sample) =>
            {
                sample.GetSampleTime(out var time);
                sample.GetSampleDuration(out var duration);
                if (isAudio) audio++; else video++;
                end = Math.Max(end, time + duration);
            });
            return new(video, audio, end / (double)TimeSpan.TicksPerSecond, new FileInfo(path).Length);
        }
        finally
        {
            Marshal.ReleaseComObject(tracks.VideoType);
            if (tracks.AudioType is not null) Marshal.ReleaseComObject(tracks.AudioType);
            Marshal.ReleaseComObject(reader);
        }
    }

    private static (uint Video, uint? Audio, IMFMediaType VideoType, IMFMediaType? AudioType) Tracks(IMFSourceReader reader)
    {
        uint? video = null, audio = null;
        IMFMediaType? vt = null, at = null;
        try
        {
            for (var index = 0u; ; index++)
            {
                IMFMediaType type;
                try { reader.GetNativeMediaType(index, 0, out type); }
                catch (COMException ex) when (ex.HResult == NoSuchStream) { break; }
                var major = type.TryGetGuid(PInvoke.MF_MT_MAJOR_TYPE);
                if (major == PInvoke.MFMediaType_Video && video is null)
                {
                    video = index;
                    vt = type;
                }
                else if (major == PInvoke.MFMediaType_Audio && audio is null)
                {
                    audio = index;
                    at = type;
                }
                else Marshal.ReleaseComObject(type);
            }
            if (video is null || vt!.TryGetGuid(PInvoke.MF_MT_SUBTYPE) != PInvoke.MFVideoFormat_H264
                || at is not null && at.TryGetGuid(PInvoke.MF_MT_SUBTYPE) != PInvoke.MFAudioFormat_AAC)
                throw new InvalidDataException("Ожидалась запись H.264/AAC");
            return (video.Value, audio, vt!, at);
        }
        catch
        {
            if (vt is not null) Marshal.ReleaseComObject(vt);
            if (at is not null) Marshal.ReleaseComObject(at);
            throw;
        }
    }

    private static void Read(IMFSourceReader reader, uint video, uint? audio, Action<bool, IMFSample> receive)
    {
        var ended = new HashSet<uint>();
        while (ended.Count < (audio is null ? 1 : 2))
        {
            reader.ReadSample(AnyStream, 0, out var stream, out var flags, out _, out var sample);
            try
            {
                if ((flags & 1) != 0) throw new InvalidDataException("Ошибка чтения медиадорожки");
                if (stream != video && stream != audio) continue;
                if ((flags & 2) != 0) ended.Add(stream);
                if (sample is not null) receive(stream == audio, sample);
            }
            finally
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
            }
        }
    }
}
