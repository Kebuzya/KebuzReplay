using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace Kebuz.Recorder.Watching;

internal readonly record struct RunningProcess(uint Id, string Executable);

internal static class ProcessScanner
{
    public static void Snapshot(List<RunningProcess> into)
    {
        into.Clear();

        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS,
            0);

        if (snapshot.IsInvalid)
        {
            return;
        }

        var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };

        if (!PInvoke.Process32FirstW(snapshot, ref entry))
        {
            return;
        }

        do
        {
            var name = entry.szExeFile.AsReadOnlySpan().SliceAtNull().ToString();

            if (name.Length != 0)
            {
                into.Add(new RunningProcess(entry.th32ProcessID, name));
            }
        }
        while (PInvoke.Process32NextW(snapshot, ref entry));
    }

    public static string? TryGetPath(uint processId)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);

        if (process.IsInvalid)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[520];
        var size = (uint)buffer.Length;

        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size)
            ? buffer[..(int)size].ToString()
            : null;
    }
}
