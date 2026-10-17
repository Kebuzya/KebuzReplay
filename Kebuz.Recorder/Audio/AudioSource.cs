using System.Runtime.InteropServices;
using Windows.Win32.Media.Audio;

namespace Kebuz.Recorder.Audio;

internal enum AudioSourceKind
{
    Desktop,
    Process,
    Microphone
}

internal enum AudioSourceState
{
    Idle,
    Live,
    Lost
}

internal abstract class AudioSource : IDisposable
{
    protected const int AccessDenied = unchecked((int)0x80070005);
    protected const int UnsupportedFormat = unchecked((int)0x88890008);
    protected const int DeviceInvalidated = unchecked((int)0x88890004);

    private readonly Lock gate = new();
    private readonly Action<string> trace;

    private AudioCapture? capture;
    private MicrophoneFilter? filter;
    private AudioSourceRing? ring;
    private float gain = 1f;
    private int level = 100;
    private bool disposed;
    private volatile bool reopen;

    protected AudioSource(AudioSourceKind kind, string label, Action<string> trace)
    {
        Kind = kind;
        Label = label;
        this.trace = trace;
    }

    public AudioSourceKind Kind { get; }

    public string Label { get; }

    public string Title => Localization.Strings.Render(Label);

    public string? DeviceName { get; private set; }

    public AudioSourceState State { get; private set; } = AudioSourceState.Idle;

    public string? Complaint { get; private set; }

    public bool Hopeless { get; private set; }

    public AudioStreamFormat Native { get; protected set; }

    public Action<AudioSource>? Changed { get; set; }

    public bool Game { get; init; }

    public AudioSourceRing Ring => ring ?? throw new InvalidOperationException($"{Title}: кольцо не заведено");

    public int Level
    {
        get => level;
        set
        {
            level = Math.Clamp(value, 0, Settings.AudioSettings.MaxLevel);
            gain = AudioGain.Of(level);
        }
    }

    public float Gain => gain;

    protected abstract bool Loopback { get; }

    protected abstract bool EventDriven { get; }

    public void Bind(AudioSourceRing bound)
    {
        ring = bound;
    }

    public bool Open()
    {
        lock (gate)
        {
            if (capture is not null)
            {
                return true;
            }

            IAudioClient? client = null;

            try
            {
                client = Activate(out var name);

                var opened = AudioCapture.Open(
                    client,
                    name,
                    new AudioCaptureRequest(AudioStreamFormat.Mix, Loopback, EventDriven, Take, trace));

                client = null;

                filter = TryFilter();

                Ring.Realign();
                opened.Start();

                capture = opened;
                DeviceName = name;

                Settle(AudioSourceState.Live, null);
                return true;
            }
            catch (Exception ex)
            {
                if (client is not null)
                {
                    Marshal.ReleaseComObject(client);
                }

                Hopeless = Fatal(ex);
                Settle(AudioSourceState.Lost, Explain(ex));
                return false;
            }
        }
    }

    public void Close()
    {
        lock (gate)
        {
            capture?.Dispose();
            capture = null;
            filter?.Dispose();
            filter = null;
        }
    }

    public void Reopen()
    {
        reopen = true;
    }

    public void Tend()
    {
        if (reopen)
        {
            reopen = false;
            trace($"{Title}: переоткрываю для нового процесса");
            Close();
            Settle(AudioSourceState.Lost, null);
        }

        if (Hopeless)
        {
            return;
        }

        if (State is AudioSourceState.Live)
        {
            var lost = capture?.Failure;

            if (lost is not null)
            {
                trace($"{Title}: источник пропал ({Explain(lost)})");
                Close();
                Settle(AudioSourceState.Lost, Explain(lost));
            }
            else if (Displaced())
            {
                trace($"{Title}: устройство по умолчанию сменилось, переоткрываю");
                Close();
                Settle(AudioSourceState.Lost, null);
            }
            else
            {
                return;
            }
        }

        if (State is AudioSourceState.Lost && Open())
        {
            trace($"{Title}: источник вернулся, {DeviceName}");
        }
    }

    protected abstract IAudioClient Activate(out string name);

    protected virtual bool Denoise => false;

    protected virtual bool Displaced()
    {
        return false;
    }

    protected virtual bool Fatal(Exception failure)
    {
        return false;
    }

    protected virtual string Explain(Exception failure)
    {
        return failure switch
        {
            COMException { HResult: AccessDenied } => Localization.Strings.Code("Audio.AccessDenied", Label),
            COMException { HResult: UnsupportedFormat } =>
                Localization.Strings.Code("Audio.UnsupportedFormat", Label, AudioStreamFormat.Mix.ToString()),
            COMException { HResult: DeviceInvalidated } => Localization.Strings.Code("Audio.DeviceGone", Label),
            _ => failure.Message
        };
    }

    private void Settle(AudioSourceState next, string? complaint)
    {
        if (State == next && Complaint == complaint)
        {
            return;
        }

        State = next;
        Complaint = complaint;
        Changed?.Invoke(this);
    }

    private void Take(ReadOnlySpan<byte> samples, int frames, long qpcTicks, AudioPacketFlags flags)
    {
        if (flags.HasFlag(AudioPacketFlags.Silent))
        {
            return;
        }

        var discontinuity = flags.HasFlag(AudioPacketFlags.Discontinuity);

        if (filter is { } active)
        {
            active.Push(MemoryMarshal.Cast<byte, float>(samples), frames, qpcTicks, discontinuity, Ring.Write);
            return;
        }

        Ring.Write(MemoryMarshal.Cast<byte, float>(samples), frames, qpcTicks, discontinuity);
    }

    private MicrophoneFilter? TryFilter()
    {
        if (!Denoise)
        {
            return null;
        }

        try
        {
            var created = new MicrophoneFilter();
            trace($"{Title}: шумоподавление включено");
            return created;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Error($"{Title}: шумоподавление не поднялось, пишу без него", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Close();
    }
}

internal sealed class DeviceAudioSource : AudioSource
{
    private readonly EDataFlow flow;
    private readonly bool loopback;
    private readonly string? wanted;
    private readonly bool denoise;

    private string? openedId;

    public DeviceAudioSource(
        AudioSourceKind kind,
        EDataFlow flow,
        bool loopback,
        string? wanted,
        string title,
        Action<string> trace,
        bool denoise = false)
        : base(kind, title, trace)
    {
        this.flow = flow;
        this.loopback = loopback;
        this.wanted = wanted;
        this.denoise = denoise;
    }

    protected override bool Loopback => loopback;

    protected override bool Denoise => denoise;

    protected override bool EventDriven => false;

    protected override IAudioClient Activate(out string name)
    {
        var device = AudioEndpoints.Activate(flow, wanted);

        openedId = device.Id;
        Native = device.Mix;
        name = device.Name;

        return device.Client;
    }

    protected override bool Displaced()
    {
        return wanted is null
            && AudioEndpoints.DefaultId(flow) is { } current
            && current != openedId;
    }

    protected override string Explain(Exception failure)
    {
        if (failure is InvalidOperationException)
        {
            return failure.Message;
        }

        if (Kind is AudioSourceKind.Microphone && failure is COMException { HResult: AccessDenied })
        {
            return "Микрофон есть, но система не даёт к нему доступ";
        }

        return base.Explain(failure);
    }
}

internal sealed class ProcessAudioSource : AudioSource
{
    private const int NotImplemented = unchecked((int)0x80004001);

    private readonly Func<uint?> locate;

    public ProcessAudioSource(Func<uint?> locate, string title, Action<string> trace)
        : base(AudioSourceKind.Process, title, trace)
    {
        this.locate = locate;
        Native = AudioStreamFormat.Mix;
    }

    protected override bool Loopback => true;

    protected override bool EventDriven => true;

    protected override IAudioClient Activate(out string name)
    {
        var processId = locate()
            ?? throw new InvalidOperationException($"{Title}: приложение не запущено");

        name = $"процесс {processId}";
        return ProcessLoopback.Activate(processId);
    }

    protected override bool Fatal(Exception failure)
    {
        return failure.HResult == NotImplemented;
    }

    protected override string Explain(Exception failure)
    {
        if (failure.HResult == NotImplemented)
        {
            return AudioDevices.PerProcessRequirement;
        }

        return failure is InvalidOperationException ? failure.Message : base.Explain(failure);
    }
}
