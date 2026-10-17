using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Encoding;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Settings;

public sealed class QualityModel : INotifyPropertyChanged, ISection
{
    private readonly Func<KebuzSettings> effective;
    private readonly Action touched;
    private readonly Action commit;

    private QualityPreset preset = KebuzSettings.Default.Quality;
    private double height = KebuzSettings.Default.CustomHeight;
    private double frameRate = KebuzSettings.Default.CustomFrameRate;
    private double mbps = KebuzSettings.Default.CustomBitrateKbps / 1000d;
    private CaptureMode capture = KebuzSettings.Default.Capture;

    public QualityModel(Func<KebuzSettings> effective, Action touched, Action commit)
    {
        this.effective = effective;
        this.touched = touched;
        this.commit = commit;
        Loc.Track(this, static owner => ((QualityModel)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SampleModel? Sample { get; init; }

    public KebuzSettings Effective => effective();

    public QualityPreset Preset => preset;

    public uint Height => (uint)Math.Round(height);

    public uint FrameRate => (uint)Math.Round(frameRate);

    public uint Kbps => (uint)Math.Round(mbps * 1000);

    public CaptureMode Capture => capture;

    public QualityOverride Override => new(preset, Height, FrameRate, Kbps, capture);

    public bool IsStandardCapture
    {
        get => capture == CaptureMode.Standard;
        set => Choose(CaptureMode.Standard, value);
    }

    public bool IsBorderless
    {
        get => capture == CaptureMode.Borderless;
        set => Choose(CaptureMode.Borderless, value);
    }

    public double MinHeight => QualityPresets.MinHeight;

    public double MaxHeight => QualityPresets.MaxHeight;

    public double HeightStep => QualityPresets.HeightStep;

    public double MinFrameRate => QualityPresets.MinFrameRate;

    public double MaxFrameRate => QualityPresets.MaxFrameRate;

    public double MinMbps => QualityPresets.MinBitrateKbps / 1000d;

    public double MaxMbps => QualityPresets.MaxBitrateKbps / 1000d;

    public bool IsCompact
    {
        get => preset == QualityPreset.Compact;
        set => Select(QualityPreset.Compact, value);
    }

    public bool IsBalanced
    {
        get => preset == QualityPreset.Balanced;
        set => Select(QualityPreset.Balanced, value);
    }

    public bool IsMax
    {
        get => preset == QualityPreset.Max;
        set => Select(QualityPreset.Max, value);
    }

    public bool IsCustom
    {
        get => preset == QualityPreset.Custom;
        set => Select(QualityPreset.Custom, value);
    }

    public double CustomHeight
    {
        get => height;
        set
        {
            if (Math.Abs(height - value) < 0.5)
            {
                return;
            }

            height = value;
            Raise(nameof(CustomHeight));
            Raise(nameof(CustomHeightText));
            touched();
        }
    }

    public double CustomFrameRate
    {
        get => frameRate;
        set
        {
            if (Math.Abs(frameRate - value) < 0.5)
            {
                return;
            }

            frameRate = value;
            Raise(nameof(CustomFrameRate));
            Raise(nameof(CustomFrameRateText));
            touched();
        }
    }

    public double CustomMbps
    {
        get => mbps;
        set
        {
            if (Math.Abs(mbps - value) < 0.05)
            {
                return;
            }

            mbps = value;
            Raise(nameof(CustomMbps));
            Raise(nameof(CustomMbpsText));
            touched();
        }
    }

    public string CustomHeightText => Strings.Format("Settings.Quality.HeightValue", Height);

    public string CustomFrameRateText => string.Create(Strings.Culture, $"{FrameRate}");

    public string CustomMbpsText => Strings.Format("Settings.Quality.Mbps", mbps);

    public string ResolutionText => Formats.Resolution(effective().Profile);

    public string FrameRateText => Strings.Format("Settings.Quality.FrameRateValue", effective().Profile.FrameRate);

    public void Adopt(QualityPreset next, uint nextHeight, uint nextFrameRate, uint nextKbps, CaptureMode nextCapture)
    {
        preset = next;
        capture = nextCapture;
        height = nextHeight;
        frameRate = nextFrameRate;
        mbps = nextKbps / 1000d;

        Raise(nameof(CustomHeight));
        Raise(nameof(CustomFrameRate));
        Raise(nameof(CustomMbps));
        Raise(nameof(CustomHeightText));
        Raise(nameof(CustomFrameRateText));
        Raise(nameof(CustomMbpsText));
        RaisePreset();
        RaiseCapture();
        Refresh();
    }

    public void Refresh()
    {
        Raise(nameof(ResolutionText));
        Raise(nameof(FrameRateText));
    }

    public void Commit()
    {
        commit();
    }

    private void Select(QualityPreset next, bool value)
    {
        if (!value || preset == next)
        {
            return;
        }

        preset = next;
        RaisePreset();
        touched();
        commit();
    }

    private void Choose(CaptureMode next, bool value)
    {
        if (!value || capture == next)
        {
            return;
        }

        capture = next;
        RaiseCapture();
        touched();
        commit();
    }

    private void RaiseCapture()
    {
        Raise(nameof(IsStandardCapture));
        Raise(nameof(IsBorderless));
    }

    private void RaisePreset()
    {
        Raise(nameof(IsCompact));
        Raise(nameof(IsBalanced));
        Raise(nameof(IsMax));
        Raise(nameof(IsCustom));
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
