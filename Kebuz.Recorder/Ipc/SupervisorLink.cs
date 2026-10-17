using System.IO.Pipes;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Settings;

namespace Kebuz.Recorder.Ipc;

public sealed class SupervisorLink : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamWriter writer;
    private readonly StreamReader reader;
    private readonly Thread listener;
    private readonly Lock gate = new();
    private readonly ManualResetEventSlim settingsArrived = new(false);

    private Action? stop;
    private Action<KebuzSettings>? settingsChanged;
    private Action? markClip;
    private Action<bool>? levelWatch;
    private Action? applyNow;
    private bool levelsWanted;
    private KebuzSettings? settings;
    private KebuzSettings? handed;
    private bool shutdownSeen;
    private volatile bool closing;

    private SupervisorLink(NamedPipeClientStream pipe)
    {
        this.pipe = pipe;
        writer = new StreamWriter(pipe, PipeProtocol.Encoding) { AutoFlush = true };
        reader = new StreamReader(pipe, PipeProtocol.Encoding);

        listener = new Thread(Listen)
        {
            Name = "KebuzSupervisorLink",
            IsBackground = true
        };

        listener.Start();
    }

    public static SupervisorLink? Connect(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            PipeNames.RecorderPipe,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error($"Труба супервизора {PipeNames.RecorderPipe} недоступна", ex);
            pipe.Dispose();
            return null;
        }

        Log.Trace($"труба супервизора подключена: {PipeNames.RecorderPipe}");
        return new SupervisorLink(pipe);
    }

    public void SignalReady()
    {
        Send(new PipeMessage(PipeMessages.Ready));
    }

    public KebuzSettings? WaitForSettings(TimeSpan timeout)
    {
        settingsArrived.Wait(timeout);

        lock (gate)
        {
            handed = settings;
            return settings;
        }
    }

    public void WhenSettings(Action<KebuzSettings> action)
    {
        KebuzSettings? late;

        lock (gate)
        {
            settingsChanged = action;
            late = ReferenceEquals(settings, handed) ? null : settings;
        }

        if (late is not null)
        {
            Log.Report("настройки пришли после ожидания, применяю их сейчас");
            action(late);
        }
    }

    public void WhenMarkClip(Action action)
    {
        lock (gate)
        {
            markClip = action;
        }
    }

    public void WhenApplyNow(Action action)
    {
        lock (gate)
        {
            applyNow = action;
        }
    }

    public void WhenLevelWatch(Action<bool> action)
    {
        bool wanted;

        lock (gate)
        {
            levelWatch = action;
            wanted = levelsWanted;
        }

        if (wanted)
        {
            action(true);
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

    public void WhenShutdown(Action action)
    {
        var immediate = false;

        lock (gate)
        {
            stop = action;
            immediate = shutdownSeen;
        }

        if (immediate)
        {
            action();
        }
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

            if (message.Type is not PipeMessages.AudioLevels)
            {
                Log.Trace($"супервизору отправлено {message.Type}");
            }
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

                Log.Trace($"от супервизора получено {message.Type}");

                switch (message.Type)
                {
                    case PipeMessages.Shutdown:
                        Signal();
                        break;

                    case PipeMessages.UpdateSettings:
                        Accept(PipeProtocol.Unpack<KebuzSettings>(message));
                        break;

                    case PipeMessages.MarkClip:
                        Mark();
                        break;

                    case PipeMessages.ApplySettingsNow:
                        Apply();
                        break;

                    case PipeMessages.WatchAudioLevels:
                        Watch(PipeProtocol.Unpack<LevelWatch>(message)?.On ?? false);
                        break;
                }
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

    private void Accept(KebuzSettings? next)
    {
        if (next is null)
        {
            Log.Error("настройки от супервизора не разобрались, остаюсь на прежних");
            return;
        }

        Action<KebuzSettings>? action;

        lock (gate)
        {
            settings = next;
            action = settingsChanged;
        }

        settingsArrived.Set();
        action?.Invoke(next);
    }

    private void Mark()
    {
        Action? action;

        lock (gate)
        {
            action = markClip;
        }

        action?.Invoke();
    }

    private void Apply()
    {
        Action? action;

        lock (gate)
        {
            action = applyNow;
        }

        action?.Invoke();
    }

    private void Watch(bool on)
    {
        Action<bool>? action;

        lock (gate)
        {
            levelsWanted = on;
            action = levelWatch;
        }

        action?.Invoke(on);
    }

    private void Signal()
    {
        Action? action;

        lock (gate)
        {
            shutdownSeen = true;
            action = stop;
        }

        action?.Invoke();
    }
}
