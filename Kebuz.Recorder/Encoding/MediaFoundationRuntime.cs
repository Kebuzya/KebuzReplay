using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Kebuz.Recorder.Encoding;

internal static class MediaFoundationRuntime
{
    private const uint ApiVersion = 0x00020070;
    private const uint NoSocket = 1;

    private static bool started;

    public static void EnsureStarted()
    {
        if (started)
        {
            return;
        }

        PInvoke.MFStartup(ApiVersion, NoSocket).ThrowOnFailure();
        started = true;
    }

    public static void Shutdown()
    {
        if (!started)
        {
            return;
        }

        started = false;
        PInvoke.MFShutdown();
    }
}

internal static class MediaFoundationAttributes
{
    public static unsafe void SetGuidValue(this IMFAttributes attributes, Guid key, Guid value)
    {
        attributes.SetGUID(&key, &value);
    }

    public static unsafe void SetUInt32Value(this IMFAttributes attributes, Guid key, uint value)
    {
        attributes.SetUINT32(&key, value);
    }

    public static unsafe void SetUInt64Value(this IMFAttributes attributes, Guid key, ulong value)
    {
        attributes.SetUINT64(&key, value);
    }

    public static unsafe void SetUnknownValue(this IMFAttributes attributes, Guid key, object value)
    {
        attributes.SetUnknown(&key, value);
    }

    public static void SetSizeValue(this IMFAttributes attributes, Guid key, uint width, uint height)
    {
        attributes.SetUInt64Value(key, ((ulong)width << 32) | height);
    }

    public static void SetRatioValue(this IMFAttributes attributes, Guid key, uint numerator, uint denominator)
    {
        attributes.SetUInt64Value(key, ((ulong)numerator << 32) | denominator);
    }

    public static uint? TryGetUInt32(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetUINT32(key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static ulong? TryGetUInt64(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetUINT64(key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static Guid? TryGetGuid(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetGUID(key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static unsafe string? TryGetString(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetAllocatedString(key, out var value, out _);

            try
            {
                return value.ToString();
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)value.Value);
            }
        }
        catch (COMException)
        {
            return null;
        }
    }
}
