using System.Diagnostics;
using System.Runtime.InteropServices;
using Kebuz.Recorder.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace Kebuz.App.Supervising;

internal sealed class ChildJob : IDisposable
{
    private readonly SafeFileHandle? handle;

    private ChildJob(SafeFileHandle? handle)
    {
        this.handle = handle;
    }

    public static ChildJob Create()
    {
        SafeFileHandle job;

        try
        {
            job = PInvoke.CreateJobObject(null, null);
        }
        catch (Exception ex)
        {
            Log.Error("не удалось создать job object, дети переживут смерть супервизора", ex);
            return new ChildJob(null);
        }

        if (job.IsInvalid)
        {
            Log.Error(
                $"CreateJobObject отказал, код {Marshal.GetLastWin32Error()}, " +
                "дети переживут смерть супервизора");
            job.Dispose();
            return new ChildJob(null);
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        var payload = MemoryMarshal.AsBytes(new ReadOnlySpan<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(in info));

        if (!PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, payload))
        {
            Log.Error(
                $"SetInformationJobObject отказал, код {Marshal.GetLastWin32Error()}, " +
                "дети переживут смерть супервизора");
            job.Dispose();
            return new ChildJob(null);
        }

        Log.Trace("job object создан, дети будут погашены вместе с супервизором");
        return new ChildJob(job);
    }

    public void Assign(Process process)
    {
        if (handle is null)
        {
            return;
        }

        try
        {
            if (PInvoke.AssignProcessToJobObject(handle, process.SafeHandle))
            {
                Log.Trace($"pid {process.Id} назначен в job object");
                return;
            }

            Log.Error($"не удалось назначить pid {process.Id} в job object, код {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Error($"не удалось назначить pid {process.Id} в job object", ex);
        }
    }

    public void Dispose()
    {
        handle?.Dispose();
    }
}
