using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Kebuz.Recorder.Diagnostics;

internal enum LogLevel
{
    Report,
    Trace,
    Error
}

public static class Log
{
    private const int QueueCapacity = 4096;
    private const int BatchLimit = 512;
    private const long RotateBytes = 5L * 1024 * 1024;

    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentQueue<Entry> Pending = new();
    private static readonly ManualResetEventSlim Wake = new(false);
    private static readonly Lock Gate = new();

    private static volatile bool console = true;
    private static volatile bool consoleTrace;
    private static volatile string fileName = "kebuz-log.txt";
    private static Thread? worker;
    private static StreamWriter? writer;
    private static bool unavailable;
    private static int queued;
    private static int dropped;

    public static string DefaultPath => Path.Combine(Settings.AppPaths.Local, fileName);

    public static string PreviousPath => Path.Combine(
        Settings.AppPaths.Local,
        $"{Path.GetFileNameWithoutExtension(fileName)}.1{Path.GetExtension(fileName)}");

    public static void Configure(bool toConsole, bool traceToConsole, string? file = null)
    {
        console = toConsole;
        consoleTrace = traceToConsole;

        if (file is not null)
        {
            fileName = file;
        }
    }

    public static void Report(string message)
    {
        Enqueue(LogLevel.Report, message);
    }

    public static void Trace(string message)
    {
        Enqueue(LogLevel.Trace, message);
    }

    public static void Error(string message)
    {
        Enqueue(LogLevel.Error, message);
    }

    public static void Error(string message, Exception failure)
    {
        Enqueue(LogLevel.Error, $"{message}: {failure}");
    }

    public static void Flush()
    {
        if (Volatile.Read(ref worker) is null)
        {
            return;
        }

        Wake.Set();

        var start = Stopwatch.GetTimestamp();

        while ((Volatile.Read(ref queued) > 0 || Volatile.Read(ref dropped) > 0)
               && Stopwatch.GetElapsedTime(start) < FlushTimeout)
        {
            Thread.Sleep(1);
        }
    }

    private static void Enqueue(LogLevel level, string message)
    {
        if (Volatile.Read(ref queued) >= QueueCapacity)
        {
            Interlocked.Increment(ref dropped);
            return;
        }

        Interlocked.Increment(ref queued);
        Pending.Enqueue(new Entry(DateTime.Now, level, message));

        Ensure();
        Wake.Set();
    }

    private static void Ensure()
    {
        if (Volatile.Read(ref worker) is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (worker is not null)
            {
                return;
            }

            var thread = new Thread(Loop)
            {
                Name = "KebuzLog",
                IsBackground = true
            };

            worker = thread;
            thread.Start();
        }
    }

    private static void Loop()
    {
        var batch = new List<Entry>(BatchLimit);

        while (true)
        {
            Wake.Wait();
            Wake.Reset();

            try
            {
                do
                {
                    while (Pending.TryDequeue(out var entry))
                    {
                        batch.Add(entry);

                        if (batch.Count >= BatchLimit)
                        {
                            Persist(batch);
                        }
                    }

                    Persist(batch);
                }
                while (!Pending.IsEmpty || Volatile.Read(ref dropped) > 0);
            }
            catch
            {
                batch.Clear();
                Close();
                Interlocked.Exchange(ref queued, Pending.Count);
            }
        }
    }

    private static void Persist(List<Entry> batch)
    {
        var counted = batch.Count;
        var lost = Interlocked.Exchange(ref dropped, 0);

        if (counted == 0 && lost == 0)
        {
            return;
        }

        if (lost > 0)
        {
            batch.Insert(0, new Entry(
                counted == 0 ? DateTime.Now : batch[0].Time,
                LogLevel.Report,
                $"очередь лога переполнена, отброшено сообщений: {lost}"));
        }

        var sink = Sink();

        foreach (var entry in batch)
        {
            sink?.WriteLine(Line(entry));
            Echo(entry);
        }

        if (sink is not null)
        {
            sink.Flush();

            if (sink.BaseStream.Length >= RotateBytes)
            {
                Rotate();
            }
        }

        batch.Clear();
        Interlocked.Add(ref queued, -counted);
    }

    private static StreamWriter? Sink()
    {
        if (writer is not null)
        {
            return writer;
        }

        try
        {
            var stream = new FileStream(DefaultPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(true));
            unavailable = false;
        }
        catch (Exception ex)
        {
            if (!unavailable)
            {
                unavailable = true;
                Console.Error.WriteLine($"Лог-файл {DefaultPath} недоступен: {ex.Message}");
            }
        }

        return writer;
    }

    private static void Rotate()
    {
        Close();

        try
        {
            File.Delete(PreviousPath);
            File.Move(DefaultPath, PreviousPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Close()
    {
        try
        {
            writer?.Dispose();
        }
        catch (IOException)
        {
        }

        writer = null;
    }

    private static void Echo(Entry entry)
    {
        if (!console || (entry.Level == LogLevel.Trace && !consoleTrace))
        {
            return;
        }

        var line = $"[{entry.Time:HH:mm:ss.fff}] {entry.Message}";

        if (entry.Level == LogLevel.Error)
        {
            Console.Error.WriteLine(line);
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    private static string Line(Entry entry)
    {
        return $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {Tag(entry.Level),-6} {entry.Message}";
    }

    private static string Tag(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Error => "error",
            _ => "report"
        };
    }

    private readonly record struct Entry(DateTime Time, LogLevel Level, string Message);
}
