using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace Kebuz.Recorder.Audio;

internal static class ProcessLoopback
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    public static bool Supported()
    {
        try
        {
            var client = Activate((uint)Environment.ProcessId);
            Marshal.ReleaseComObject(client);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static unsafe IAudioClient Activate(uint processId)
    {
        var parameters = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        };

        parameters.ProcessLoopbackParams.TargetProcessId = processId;
        parameters.ProcessLoopbackParams.ProcessLoopbackMode =
            PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

        var blob = default(PROPVARIANT);
        blob.vt = VARENUM.VT_BLOB;
        blob.blob.cbSize = (uint)sizeof(AUDIOCLIENT_ACTIVATION_PARAMS);
        blob.blob.pBlobData = (byte*)&parameters;

        var handler = new Completion();

        PInvoke.ActivateAudioInterfaceAsync(
            PInvoke.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            typeof(IAudioClient).GUID,
            blob,
            handler,
            out var operation).ThrowOnFailure();

        try
        {
            if (!handler.Wait(ActivationTimeout))
            {
                throw new TimeoutException(
                    $"Активация звука процесса {processId} не ответила за {ActivationTimeout.TotalSeconds:F0} с");
            }

            operation.GetActivateResult(out var result, out var activated);
            result.ThrowOnFailure();

            return (IAudioClient)activated;
        }
        finally
        {
            handler.Dispose();
            Marshal.ReleaseComObject(operation);
        }
    }

    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IDisposable
    {
        private readonly ManualResetEventSlim done = new(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            done.Set();
        }

        public bool Wait(TimeSpan timeout)
        {
            return done.Wait(timeout);
        }

        public void Dispose()
        {
            done.Dispose();
        }
    }
}
