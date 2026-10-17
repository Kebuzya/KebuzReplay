using System.Diagnostics;
using System.Runtime.InteropServices;
using Kebuz.Recorder.Diagnostics;
using Kebuz.Recorder.Encoding;
using Windows.Graphics.Capture;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Kebuz.Recorder.Capture;

internal sealed record CaptureRequest(
    uint Width,
    uint Height,
    uint FrameRate,
    Action<string> Trace,
    bool WatchBlank = false,
    Action<ShotImage>? Shot = null);

internal sealed record CaptureResult(
    int FramesCaptured,
    int FramesWritten,
    int FramesDropped,
    TimeSpan Elapsed);

internal sealed class CaptureRunner : IDisposable
{
    private const int QueueCapacity = 8;

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BlankInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShotDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShotPatience = TimeSpan.FromSeconds(5);
    private const int RhythmSamples = 100_000;

    private readonly CaptureRequest request;
    private readonly GraphicsDevice device;
    private readonly IVideoEncoder encoder;
    private readonly TexturePool pool;
    private readonly EncoderPump pump;
    private readonly Lock feed = new();
    private readonly Lock gate = new();

    private FrameCapture? capture;
    private BlankCheck? blank;
    private Action<ShotImage>? shot;
    private int generation;
    private Thread? darkness;
    private ManualResetEventSlim? darknessStop;
    private ID3D11Texture2D? black;
    private FrameFit? fit;
    private (uint Width, uint Height) shown;
    private int blackFrames;

    private Stopwatch? stopwatch;
    private int captured;
    private long lastIndex = -1;
    private TimeSpan? origin;
    private TimeSpan lastBlankCheck;
    private TimeSpan? blankSince;
    private TimeSpan latest;
    private TimeSpan shotFrom;
    private TimeSpan nextShot = ShotDelay;
    private bool shotTaken;
    private readonly List<double> offsets = [];
    private bool stopped;
    private bool disposed;

    public CaptureRunner(
        GraphicsDevice device,
        GraphicsCaptureItem item,
        IVideoEncoder encoder,
        CaptureRequest request)
    {
        this.request = request;
        this.device = device;
        this.encoder = encoder;

        pool = new TexturePool(device, request.Width, request.Height);
        pump = new EncoderPump(encoder, pool, QueueCapacity);
        blank = request.WatchBlank ? new BlankCheck(device) : null;
        shot = request.Shot;

        var own = generation;
        capture = new FrameCapture(device, item, frame => Accept(own, frame));
    }

    public Exception? Failure => pump.Failure;

    public TimeSpan BlankFor
    {
        get
        {
            lock (gate)
            {
                return blankSince is { } since ? latest - since : TimeSpan.Zero;
            }
        }
    }

    public void Start()
    {
        request.Trace($"стартую захват, {capture?.Border}");
        stopwatch = Stopwatch.StartNew();
        capture?.Start();
    }

    public void Blackout()
    {
        FrameCapture? old;
        BlankCheck? oldBlank;

        lock (feed)
        {
            if (stopped || darkness is not null)
            {
                return;
            }

            var own = ++generation;
            old = capture;
            oldBlank = blank;
            capture = null;
            blank = null;
            shot = null;
            blackFrames = 0;

            lock (gate)
            {
                blankSince = null;
            }

            var stop = new ManualResetEventSlim(false);
            darknessStop = stop;
            darkness = new Thread(() => Darken(own, stop))
            {
                Name = "KebuzBlackFrames",
                IsBackground = true
            };
            darkness.SetApartmentState(ApartmentState.MTA);
            darkness.Start();
        }

        old?.Dispose();
        oldBlank?.Dispose();
        request.Trace($"окна нет, пишу чёрный кадр с индекса {Interlocked.Read(ref lastIndex) + 1}");
    }

    public void Attach(GraphicsCaptureItem item, bool watchBlank, Action<ShotImage>? nextShotHandler)
    {
        FrameCapture? old;
        BlankCheck? oldBlank;
        Thread? dark;
        ManualResetEventSlim? darkStop;
        FrameCapture fresh;
        int blackCount;

        lock (feed)
        {
            if (stopped)
            {
                return;
            }

            var own = ++generation;
            old = capture;
            oldBlank = blank;
            dark = darkness;
            darkStop = darknessStop;
            darkness = null;
            darknessStop = null;
            blackCount = blackFrames;

            fresh = new FrameCapture(device, item, frame => Accept(own, frame));
            capture = fresh;
            blank = watchBlank ? new BlankCheck(device) : null;
            shot = nextShotHandler;

            lock (gate)
            {
                blankSince = null;
                shotTaken = false;
                shotFrom = origin is { } start ? Now() - start : TimeSpan.Zero;
                nextShot = shotFrom + ShotDelay;
            }
        }

        darkStop?.Set();
        dark?.Join();
        darkStop?.Dispose();
        old?.Dispose();
        oldBlank?.Dispose();

        request.Trace($"захват окна снова идёт, чёрных кадров было {blackCount}, {fresh.Border}");
        fresh.Start();
    }

    private void Darken(int own, ManualResetEventSlim stop)
    {
        var interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / request.FrameRate / 2);

        while (!stop.Wait(interval))
        {
            lock (feed)
            {
                if (own != generation)
                {
                    return;
                }

                try
                {
                    Feed(Now(), null);
                }
                catch (Exception ex)
                {
                    request.Trace($"чёрный кадр не записан: {ex.Message}");
                }
            }
        }
    }

    private void Accept(int own, CapturedFrame frame)
    {
        lock (feed)
        {
            if (own != generation)
            {
                return;
            }

            Feed(frame.Timestamp, frame.Texture, (uint)Math.Max(0, frame.ContentSize.Width), (uint)Math.Max(0, frame.ContentSize.Height));
        }
    }

    private void Feed(TimeSpan timestamp, ID3D11Texture2D? source, uint contentWidth = 0, uint contentHeight = 0)
    {
        long index;
        var first = false;

        lock (gate)
        {
            if (stopped)
            {
                return;
            }

            if (source is not null)
            {
                captured++;
            }

            if (origin is null)
            {
                origin = timestamp;
                first = true;
            }

            var elapsed = timestamp - origin.Value;
            index = (long)((elapsed.TotalSeconds * request.FrameRate) + 0.5);
            if (index <= lastIndex)
            {
                return;
            }

            Interlocked.Exchange(ref lastIndex, index);

            if (source is not null && offsets.Count < RhythmSamples)
            {
                offsets.Add((elapsed.TotalSeconds - ((double)index / request.FrameRate)) * 1000);
            }
        }

        if (first)
        {
            encoder.Anchor(timestamp.Ticks);
        }

        var texture = pool.Rent();

        if (source is null)
        {
            device.Context.CopyResource((ID3D11Resource)texture, (ID3D11Resource)Black());
            blackFrames++;
        }
        else
        {
            source.GetDesc(out var desc);
            var width = contentWidth == 0 ? desc.Width : Math.Min(desc.Width, contentWidth);
            var height = contentHeight == 0 ? desc.Height : Math.Min(desc.Height, contentHeight);

            if (shown != (width, height))
            {
                if (shown != default)
                {
                    request.Trace($"окно сменило размер: {width}x{height}, кадр записи {request.Width}x{request.Height}");
                }

                shown = (width, height);
            }

            Watch(timestamp, source, width, height);
            Snap(source, timestamp, width, height);

            if (FrameFit.Same(width, height, request.Width, request.Height))
            {
                device.CopyRegion(texture, source, request.Width, request.Height);
            }
            else
            {
                fit ??= new FrameFit(device, request.Width, request.Height);
                fit.Draw(texture, source, width, height);
            }
        }

        pump.Submit(new PendingFrame(texture, index * TimeSpan.TicksPerSecond / request.FrameRate));
    }

    private static TimeSpan Now()
    {
        return TimeSpan.FromTicks(Qpc.Ticks());
    }

    private unsafe ID3D11Texture2D Black()
    {
        if (black is not null)
        {
            return black;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = request.Width,
            Height = request.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };

        var zeros = new byte[request.Width * request.Height * 4];

        fixed (byte* pixels = zeros)
        {
            var data = new D3D11_SUBRESOURCE_DATA
            {
                pSysMem = pixels,
                SysMemPitch = request.Width * 4,
                SysMemSlicePitch = 0
            };

            device.Device.CreateTexture2D(desc, data, out var created);
            black = created;
        }

        return black;
    }

    private string Rhythm()
    {
        List<double> sorted;

        lock (gate)
        {
            sorted = offsets.Select(Math.Abs).OrderBy(value => value).ToList();
        }

        if (sorted.Count == 0)
        {
            return "ритм: кадров нет";
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"ритм: отклонение взятых кадров от сетки медиана {sorted[sorted.Count / 2]:F2} мс, " +
            $"95% {sorted[(int)(sorted.Count * 0.95)]:F2} мс, максимум {sorted[^1]:F2} мс, " +
            $"кадров от WGC {captured}");
    }

    private void Watch(TimeSpan timestamp, ID3D11Texture2D source, uint width, uint height)
    {
        if (blank is null)
        {
            return;
        }

        lock (gate)
        {
            latest = timestamp;

            if (timestamp - lastBlankCheck < BlankInterval)
            {
                return;
            }

            lastBlankCheck = timestamp;
        }

        var dark = blank.IsBlank(source, width, height);

        lock (gate)
        {
            if (!dark)
            {
                blankSince = null;
            }
            else
            {
                blankSince ??= timestamp;
            }
        }
    }

    private void Snap(ID3D11Texture2D source, TimeSpan timestamp, uint width, uint height)
    {
        if (shot is null)
        {
            return;
        }

        TimeSpan since;
        TimeSpan from;

        lock (gate)
        {
            if (shotTaken || origin is null)
            {
                return;
            }

            since = timestamp - origin.Value;
            from = shotFrom;

            if (since < nextShot)
            {
                return;
            }

            nextShot = since + ShotDelay;
        }

        ShotImage image;

        try
        {
            image = FrameShot.Take(device, source, width, height);
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                shotTaken = true;
            }

            request.Trace($"кадр окна не снят: {ex.Message}");
            return;
        }

        if (image.Dark && since - from < ShotPatience)
        {
            return;
        }

        lock (gate)
        {
            shotTaken = true;
        }

        shot(image);
    }

    public CaptureResult Stop()
    {
        FrameCapture? closing;
        Thread? dark;
        ManualResetEventSlim? darkStop;

        lock (feed)
        {
            lock (gate)
            {
                stopped = true;
            }

            closing = capture;
            capture = null;
            dark = darkness;
            darkStop = darknessStop;
            darkness = null;
            darknessStop = null;
            generation++;
        }

        darkStop?.Set();
        dark?.Join();
        darkStop?.Dispose();

        stopwatch?.Stop();
        closing?.Dispose();
        request.Trace($"захват остановлен, кадров {captured}, закодировано {pump.Written}, отброшено {pump.Dropped}");
        request.Trace(Rhythm());

        request.Trace("дожидаюсь энкодера");
        var completed = pump.TryComplete(StallTimeout);
        var failure = pump.Failure;

        if (!completed)
        {
            throw new TimeoutException(
                $"Энкодер не отвечает дольше {StallTimeout.TotalSeconds:F0} с. " +
                $"Кадров захвачено {captured}, закодировано {pump.Written}, отброшено {pump.Dropped}");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"Кадров захвачено {captured}, закодировано {pump.Written}, кодирование прервано",
                failure);
        }

        return new CaptureResult(
            captured,
            pump.Written,
            pump.Dropped,
            stopwatch?.Elapsed ?? TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        capture?.Dispose();
        darknessStop?.Set();
        darkness?.Join();
        darknessStop?.Dispose();
        pump.Dispose();
        fit?.Dispose();
        pool.Dispose();
        blank?.Dispose();

        if (black is not null)
        {
            Marshal.ReleaseComObject(black);
        }
    }
}
