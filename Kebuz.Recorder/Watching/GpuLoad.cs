using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Performance;

namespace Kebuz.Recorder.Watching;

internal sealed unsafe class GpuLoad : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const PDH_FMT NoCap = (PDH_FMT)0x8000;

    private PDH_HQUERY query;
    private PDH_HCOUNTER counter;
    private byte[] buffer = new byte[4096];

    public GpuLoad(uint processId)
    {
        PDH_HQUERY opened;

        if (PInvoke.PdhOpenQuery(new PCWSTR(null), 0, &opened) != 0)
        {
            return;
        }

        query = opened;

        PDH_HCOUNTER added;

        fixed (char* path = $"\\GPU Engine(pid_{processId}_*engtype_3D)\\Utilization Percentage")
        {
            if (PInvoke.PdhAddEnglishCounter(query, new PCWSTR(path), 0, &added) != 0)
            {
                return;
            }
        }

        counter = added;
        PInvoke.PdhCollectQueryData(query);
    }

    public double? Sample()
    {
        if (counter == default)
        {
            return null;
        }

        if (PInvoke.PdhCollectQueryData(query) != 0)
        {
            return null;
        }

        var size = 0u;
        var count = 0u;
        var status = PInvoke.PdhGetFormattedCounterArray(counter, PDH_FMT.PDH_FMT_DOUBLE | NoCap, &size, &count, null);

        if (status == 0 && count == 0)
        {
            return 0;
        }

        if (status != MoreData)
        {
            return null;
        }

        if (buffer.Length < size)
        {
            buffer = new byte[size];
        }

        fixed (byte* items = buffer)
        {
            var list = (PDH_FMT_COUNTERVALUE_ITEM_W*)items;

            if (PInvoke.PdhGetFormattedCounterArray(counter, PDH_FMT.PDH_FMT_DOUBLE | NoCap, &size, &count, list) != 0)
            {
                return null;
            }

            var sum = 0d;

            for (var i = 0; i < count; i++)
            {
                if (list[i].FmtValue.CStatus <= 1)
                {
                    sum += list[i].FmtValue.doubleValue;
                }
            }

            return sum;
        }
    }

    public void Dispose()
    {
        if (query == default)
        {
            return;
        }

        PInvoke.PdhCloseQuery(query);
        query = default;
        counter = default;
    }
}
