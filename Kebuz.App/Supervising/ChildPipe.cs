using System.IO;
using System.IO.Pipes;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Ipc;

namespace Kebuz.App.Supervising;

internal sealed class ChildPipe : IDisposable
{
    private readonly string name;
    private readonly string label;
    private readonly Action<PipeMessage> received;
    private readonly Action connected;
    private readonly Thread accepting;
    private readonly Lock gate = new();

    private NamedPipeServerStream? connection;
    private StreamWriter? writer;
    private volatile bool closing;

    public ChildPipe(string name, string label, Action<PipeMessage> received, Action connected)
    {
        this.name = name;
        this.label = label;
        this.received = received;
        this.connected = connected;

        accepting = new Thread(Accept)
        {
            Name = $"KebuzPipe{label}",
            IsBackground = true
        };

        accepting.Start();
    }

    public bool Send<T>(string type, T payload)
    {
        return Send(PipeProtocol.Carry(type, payload));
    }

    public bool Forward(PipeMessage message)
    {
        return Send(message, false);
    }

    public bool Send(PipeMessage message)
    {
        return Send(message, true);
    }

    private bool Send(PipeMessage message, bool loud)
    {
        lock (gate)
        {
            if (writer is null)
            {
                if (loud)
                {
                    Log.Error($"труба {label} не подключена, {message.Type} не отправлен");
                }

                return false;
            }

            try
            {
                writer.WriteLine(PipeProtocol.Write(message));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"не удалось отправить {message.Type} в {label}", ex);
                return false;
            }
        }
    }

    public void Dispose()
    {
        closing = true;
        Drop();
    }

    private void Accept()
    {
        while (!closing)
        {
            NamedPipeServerStream server;

            try
            {
                server = new NamedPipeServerStream(
                    name,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                server.WaitForConnection();
            }
            catch (Exception ex)
            {
                if (closing)
                {
                    return;
                }

                Log.Error($"труба {name} не поднялась", ex);
                Thread.Sleep(1000);
                continue;
            }

            lock (gate)
            {
                connection = server;
                writer = new StreamWriter(server, PipeProtocol.Encoding) { AutoFlush = true };
            }

            try
            {
                connected();
            }
            catch (Exception ex)
            {
                Log.Error($"не удалось обработать подключение {label}", ex);
            }

            Serve(server);
            Drop();
        }
    }

    private void Serve(NamedPipeServerStream server)
    {
        try
        {
            using var reader = new StreamReader(server, PipeProtocol.Encoding, false, -1, true);

            while (reader.ReadLine() is { } line)
            {
                var message = PipeProtocol.Read(line);

                if (message is null)
                {
                    Log.Trace($"от {label} пришла нечитаемая строка: {line}");
                    continue;
                }

                received(message);
            }
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                Log.Trace($"труба {label} закрылась: {ex.Message}");
            }
        }
    }

    private void Drop()
    {
        NamedPipeServerStream? server;

        lock (gate)
        {
            server = connection;
            connection = null;
            writer = null;
        }

        try
        {
            server?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
