using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Kebuz.Recorder.Audio;

internal sealed record AudioEndpoint(string Id, string Name, bool Default);

internal sealed record ActivatedDevice(IAudioClient Client, string Id, string Name, AudioStreamFormat Mix);

internal static class AudioEndpoints
{
    private const int NotFound = unchecked((int)0x80070490);

    public static unsafe ActivatedDevice Activate(EDataFlow flow, string? wanted)
    {
        var enumerator = Create();
        IMMDevice? device = null;

        try
        {
            device = wanted is null ? Default(enumerator, flow) : Find(enumerator, flow, wanted);

            if (device is null)
            {
                throw new InvalidOperationException(Missing(flow, wanted));
            }

            var id = Id(device);
            var name = Name(device);

            device.Activate(typeof(IAudioClient).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
            var client = (IAudioClient)activated;

            client.GetMixFormat(out var mix);

            try
            {
                return new ActivatedDevice(client, id, name, AudioStreamFormat.Read(mix));
            }
            finally
            {
                PInvoke.CoTaskMemFree(mix);
            }
        }
        finally
        {
            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            Marshal.ReleaseComObject(enumerator);
        }
    }

    public static string? DefaultId(EDataFlow flow)
    {
        var enumerator = Create();

        try
        {
            var device = Default(enumerator, flow);

            if (device is null)
            {
                return null;
            }

            try
            {
                return Id(device);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public static string Missing(EDataFlow flow, string? wanted)
    {
        var output = flow == EDataFlow.eRender;

        if (wanted is null)
        {
            return Localization.Strings.Code(output ? "Audio.NoOutput" : "Audio.NoInput");
        }

        return wanted.StartsWith('{')
            ? Localization.Strings.Code(output ? "Audio.OutputGone" : "Audio.InputGone")
            : Localization.Strings.Code(output ? "Audio.OutputNotFound" : "Audio.InputNotFound", wanted);
    }

    public static IMMDeviceEnumerator Create()
    {
        PInvoke.CoCreateInstance<IMMDeviceEnumerator>(
            typeof(MMDeviceEnumerator).GUID,
            null,
            CLSCTX.CLSCTX_ALL,
            out var enumerator).ThrowOnFailure();

        return enumerator;
    }

    public static IMMDevice? Default(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        try
        {
            enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out var device);
            return device;
        }
        catch (COMException ex) when (ex.HResult == NotFound)
        {
            return null;
        }
    }

    public static IMMDevice? Find(IMMDeviceEnumerator enumerator, EDataFlow flow, string wanted)
    {
        enumerator.EnumAudioEndpoints(flow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var devices);

        try
        {
            devices.GetCount(out var count);

            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0u; i < count; i++)
                {
                    devices.Item(i, out var device);

                    var hit = pass == 0
                        ? string.Equals(Id(device), wanted, StringComparison.OrdinalIgnoreCase)
                        : Name(device).Contains(wanted, StringComparison.OrdinalIgnoreCase);

                    if (hit)
                    {
                        return device;
                    }

                    Marshal.ReleaseComObject(device);
                }
            }

            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(devices);
        }
    }

    public static IReadOnlyList<AudioEndpoint> List(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        var current = Default(enumerator, flow);
        var currentId = current is null ? null : Id(current);

        if (current is not null)
        {
            Marshal.ReleaseComObject(current);
        }

        enumerator.EnumAudioEndpoints(flow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var devices);

        try
        {
            devices.GetCount(out var count);
            var found = new List<AudioEndpoint>((int)count);

            for (var i = 0u; i < count; i++)
            {
                devices.Item(i, out var device);

                try
                {
                    var id = Id(device);
                    found.Add(new AudioEndpoint(id, Name(device), id == currentId));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }

            return found;
        }
        finally
        {
            Marshal.ReleaseComObject(devices);
        }
    }

    public static unsafe string Id(IMMDevice device)
    {
        device.GetId(out var id);

        try
        {
            return id.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(id.Value);
        }
    }

    public static unsafe string Name(IMMDevice device)
    {
        device.OpenPropertyStore(STGM.STGM_READ, out var store);

        try
        {
            store.GetValue(PInvoke.PKEY_Device_FriendlyName, out var value);
            var text = value.pwszVal;

            try
            {
                return text.Value is null ? Localization.Strings.Get("Audio.Unnamed") : text.ToString();
            }
            finally
            {
                PInvoke.CoTaskMemFree(text.Value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }
}
