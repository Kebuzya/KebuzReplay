using System.Runtime.InteropServices;
using Windows.Win32.Media.Audio;

namespace Kebuz.Recorder.Audio;

public sealed record AudioDevice(string Id, string Name, bool Default);

public static class AudioDevices
{
    private static readonly Lazy<bool> perProcess = new(() => Apart(ProcessLoopback.Supported));

    public static bool PerProcessSupported => perProcess.Value;

    public static string PerProcessRequirement =>
        Localization.Strings.Format("Audio.PerProcessMissing", Environment.OSVersion.Version.Build);

    public static IReadOnlyList<AudioDevice> Outputs()
    {
        return Apart(() => List(EDataFlow.eRender));
    }

    public static IReadOnlyList<AudioDevice> Inputs()
    {
        return Apart(() => List(EDataFlow.eCapture));
    }

    public static string? TryOpenMicrophone(string? device)
    {
        return Apart(() => OpenMicrophone(device));
    }

    private static IReadOnlyList<AudioDevice> List(EDataFlow flow)
    {
        var enumerator = AudioEndpoints.Create();

        try
        {
            return AudioEndpoints.List(enumerator, flow)
                .Select(endpoint => new AudioDevice(endpoint.Id, endpoint.Name, endpoint.Default))
                .ToArray();
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static string? OpenMicrophone(string? device)
    {
        IAudioClient? client = null;
        AudioCapture? capture = null;

        try
        {
            var activated = AudioEndpoints.Activate(EDataFlow.eCapture, device);
            client = activated.Client;

            capture = AudioCapture.Open(
                client,
                activated.Name,
                new AudioCaptureRequest(AudioStreamFormat.Mix, false, false, (_, _, _, _) => { }, _ => { }));

            client = null;
            return null;
        }
        catch (Exception ex)
        {
            return Explain(ex);
        }
        finally
        {
            capture?.Dispose();

            if (client is not null)
            {
                Marshal.ReleaseComObject(client);
            }
        }
    }

    private static string Explain(Exception failure)
    {
        const int accessDenied = unchecked((int)0x80070005);

        if (failure is InvalidOperationException)
        {
            return failure.Message;
        }

        return failure.HResult == accessDenied
            ? Localization.Strings.Code("Audio.MicrophoneDenied")
            : failure.Message;
    }

    private static T Apart<T>(Func<T> work)
    {
        var result = default(T)!;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            Name = "KebuzAudioDevices",
            IsBackground = true
        };

        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
