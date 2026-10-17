using System.IO;
using System.IO.Pipes;
using Kebuz.Gui.Gallery;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Shell;

public sealed class AppLink : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly NamedPipeClientStream pipe;
    private readonly StreamWriter writer;
    private readonly StreamReader reader;
    private readonly Thread listener;
    private readonly Lock gate = new();

    private volatile bool closing;
    private KebuzSettings? latest;
    private Action<KebuzSettings>? settingsHandler;
    private HotkeyState? lastHotkey;
    private Action<HotkeyState>? hotkeyHandler;

    private AppLink(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        writer = new StreamWriter(pipe, PipeProtocol.Encoding) { AutoFlush = true };
        reader = new StreamReader(pipe, PipeProtocol.Encoding);

        listener = new Thread(Listen)
        {
            Name = "KebuzAppLink",
            IsBackground = true
        };

        listener.Start();
    }

    public static AppLink? Connect()
    {
        var pipe = new NamedPipeClientStream(
            ".",
            PipeNames.GuiPipe,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error($"труба супервизора {PipeNames.GuiPipe} недоступна", ex);
            pipe.Dispose();
            return null;
        }

        Log.Report($"труба супервизора подключена: {PipeNames.GuiPipe}");
        return new AppLink(pipe);
    }

    public event Action<string>? RecorderRestarted;

    public event Action<string>? CaptureRecovered;

    public event Action<ClipEntry>? ClipSaved;

    public event Action<RecordingState>? RecordingStateChanged;

    public event Action<long>? BufferMemoryUsage;

    public event Action<SourceHealth>? AudioSourceChanged;

    public event Action<AudioLevels>? AudioLevels;

    public event Action<DetectedGame>? GameDetected;

    public event Action<CaptureShot>? CaptureShown;

    public event Action? ShowRequested;

    public void WhenHotkey(Action<HotkeyState> handler)
    {
        HotkeyState? current;

        lock (gate)
        {
            hotkeyHandler = handler;
            current = lastHotkey;
        }

        if (current is not null)
        {
            handler(current);
        }
    }

    public void WhenSettings(Action<KebuzSettings> handler)
    {
        KebuzSettings? current;

        lock (gate)
        {
            settingsHandler = handler;
            current = latest;
        }

        if (current is not null)
        {
            handler(current);
        }
    }

    public void Send(string type)
    {
        Send(new PipeMessage(type));
    }

    public void Send<T>(string type, T payload)
    {
        Send(PipeProtocol.Carry(type, payload));
    }

    public void Dispose()
    {
        closing = true;

        try
        {
            pipe.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void Send(PipeMessage message)
    {
        try
        {
            lock (gate)
            {
                writer.WriteLine(PipeProtocol.Write(message));
            }

            Log.Trace($"супервизору отправлено {message.Type}");
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось отправить {message.Type} супервизору", ex);
        }
    }

    private void Listen()
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                var message = PipeProtocol.Read(line);

                if (message is null)
                {
                    Log.Trace($"от супервизора пришла нечитаемая строка: {line}");
                    continue;
                }

                if (message.Type is not PipeMessages.AudioLevels)
                {
                    Log.Trace($"от супервизора получено {message.Type}");
                }
                Dispatch(message);
            }
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                Log.Error("труба супервизора оборвалась", ex);
            }
        }
    }

    private void Dispatch(PipeMessage message)
    {
        switch (message.Type)
        {
            case PipeMessages.UpdateSettings:
                if (PipeProtocol.Unpack<KebuzSettings>(message) is { } settings)
                {
                    Action<KebuzSettings>? handler;

                    lock (gate)
                    {
                        latest = settings;
                        handler = settingsHandler;
                    }

                    handler?.Invoke(settings);
                }

                break;

            case PipeMessages.RecorderRestarted:
                RecorderRestarted?.Invoke(
                    PipeProtocol.Unpack<RestartInfo>(message)?.Reason ?? Strings.Code("Trouble.Unknown"));
                break;

            case PipeMessages.CaptureRecovered:
                CaptureRecovered?.Invoke(
                    PipeProtocol.Unpack<RecoveryInfo>(message)?.Reason ?? Strings.Code("Trouble.Unknown"));
                break;

            case PipeMessages.ClipSaved:
                if (PipeProtocol.Unpack<ClipInfo>(message) is { } clip)
                {
                    ClipSaved?.Invoke(new ClipEntry(
                        clip.Path,
                        clip.Game,
                        Path.GetFileName(clip.SessionFolder),
                        ClipLibrary.StampOf(clip.Path),
                        TimeSpan.FromSeconds(clip.Seconds),
                        clip.SizeBytes,
                        clip.WithoutMicrophone));
                }

                break;

            case PipeMessages.RecordingStateChanged:
                if (PipeProtocol.Unpack<RecordingState>(message) is { } state)
                {
                    RecordingStateChanged?.Invoke(state);
                }

                break;

            case PipeMessages.BufferMemoryUsage:
                if (PipeProtocol.Unpack<BufferUsage>(message) is { } usage)
                {
                    BufferMemoryUsage?.Invoke(usage.Bytes);
                }

                break;

            case PipeMessages.AudioSourceChanged:
                if (PipeProtocol.Unpack<SourceHealth>(message) is { } health)
                {
                    AudioSourceChanged?.Invoke(health);
                }

                break;

            case PipeMessages.AudioLevels:
                if (PipeProtocol.Unpack<AudioLevels>(message) is { } levels)
                {
                    AudioLevels?.Invoke(levels);
                }

                break;

            case PipeMessages.GameDetected:
                if (PipeProtocol.Unpack<DetectedGame>(message) is { } game)
                {
                    GameDetected?.Invoke(game);
                }

                break;

            case PipeMessages.ShowWindow:
                ShowRequested?.Invoke();
                break;

            case PipeMessages.CaptureShown:
                if (PipeProtocol.Unpack<CaptureShot>(message) is { } shot)
                {
                    CaptureShown?.Invoke(shot);
                }

                break;

            case PipeMessages.HotkeyChanged:
                if (PipeProtocol.Unpack<HotkeyState>(message) is { } bound)
                {
                    Action<HotkeyState>? hotkey;

                    lock (gate)
                    {
                        lastHotkey = bound;
                        hotkey = hotkeyHandler;
                    }

                    hotkey?.Invoke(bound);
                }

                break;
        }
    }
}
