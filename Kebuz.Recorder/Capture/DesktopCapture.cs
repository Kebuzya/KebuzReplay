using System.Runtime.InteropServices;
using Kebuz.Recorder.Diagnostics;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Kebuz.Recorder.Capture;

internal sealed class DesktopCapture : IFrameCapture
{
    private const int NotFound = unchecked((int)0x887A0002);
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0026);

    private readonly IDXGIOutputDuplication duplication;
    private readonly ManualResetEventSlim stop = new(false);
    private Thread? thread;
    private Exception? failure;
    private bool disposed;

    private DesktopCapture(IDXGIOutputDuplication duplication, int width, int height)
    {
        this.duplication = duplication;
        Size = new SizeInt32 { Width = width, Height = height };
    }

    public SizeInt32 Size { get; }

    public string Border => "захват монитора через Desktop Duplication, рамки нет";

    public Exception? Failure => Volatile.Read(ref failure);

    public static unsafe DesktopCapture Open(nint monitor, out GraphicsDevice device)
    {
        PInvoke.CreateDXGIFactory1<IDXGIFactory1>(out var factory).ThrowOnFailure();
        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (adapterResult.Value == NotFound)
                {
                    break;
                }
                adapterResult.ThrowOnFailure();

                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                        if (outputResult.Value == NotFound)
                        {
                            break;
                        }
                        outputResult.ThrowOnFailure();

                        try
                        {
                            var description = output.GetDesc();
                            if ((nint)description.Monitor.Value != monitor)
                            {
                                continue;
                            }

                            if (description.Rotation is not DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_IDENTITY
                                and not DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_UNSPECIFIED)
                            {
                                throw new NotSupportedException("Desktop Duplication пока не поддерживает повёрнутый монитор");
                            }

                            device = GraphicsDevice.Create(adapter);
                            try
                            {
                                ((IDXGIOutput1)output).DuplicateOutput(device.Device, out var duplicated);
                                var bounds = description.DesktopCoordinates;
                                return new DesktopCapture(duplicated, bounds.right - bounds.left, bounds.bottom - bounds.top);
                            }
                            catch
                            {
                                device.Dispose();
                                throw;
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(output);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        throw new InvalidOperationException("выбранный монитор не найден среди выходов DXGI");
    }

    public void Start(Action<CapturedFrame> onFrame)
    {
        if (thread is not null)
        {
            throw new InvalidOperationException("захват монитора уже запущен");
        }

        thread = new Thread(() => Loop(onFrame))
        {
            IsBackground = true,
            Name = "KebuzDesktopCapture"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    private void Loop(Action<CapturedFrame> onFrame)
    {
        while (!stop.IsSet)
        {
            IDXGIResource? resource = null;
            var acquired = false;
            try
            {
                duplication.AcquireNextFrame(100, out var information, out resource);
                acquired = true;
                var texture = (ID3D11Texture2D)resource;
                var timestamp = information.LastPresentTime != 0
                    ? Qpc.FromTimestamp(information.LastPresentTime)
                    : Qpc.Ticks();
                onFrame(new CapturedFrame(texture, TimeSpan.FromTicks(timestamp), Size));
            }
            catch (COMException ex) when (ex.HResult == WaitTimeout)
            {
            }
            catch (COMException ex) when (ex.HResult == AccessLost)
            {
                Volatile.Write(ref failure, new IOException("доступ к выбранному монитору потерян", ex));
                return;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref failure, ex);
                return;
            }
            finally
            {
                if (resource is not null)
                {
                    Marshal.ReleaseComObject(resource);
                }
                if (acquired)
                {
                    try
                    {
                        duplication.ReleaseFrame();
                    }
                    catch (COMException ex) when (stop.IsSet || ex.HResult == AccessLost)
                    {
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref failure, ex);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stop.Set();
        thread?.Join();
        Marshal.ReleaseComObject(duplication);
        stop.Dispose();
    }
}
