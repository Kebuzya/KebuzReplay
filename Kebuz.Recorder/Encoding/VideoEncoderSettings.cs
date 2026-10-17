namespace Kebuz.Recorder.Encoding;

internal sealed record VideoEncoderSettings(
    uint SourceWidth,
    uint SourceHeight,
    uint Width,
    uint Height,
    uint FrameRate,
    uint BitrateBps,
    uint PeakBitrateBps,
    uint QualityVsSpeed)
{
    public bool Scaled => SourceWidth != Width || SourceHeight != Height;

    public static VideoEncoderSettings Create(
        uint sourceWidth,
        uint sourceHeight,
        QualityProfile profile,
        uint? frameRate = null,
        uint? bitrateBps = null)
    {
        var source = (QualityPresets.Even(sourceWidth), QualityPresets.Even(sourceHeight));
        var output = QualityPresets.OutputSize(source.Item1, source.Item2, profile.TargetHeight);
        var mean = bitrateBps ?? profile.MeanBitrateBps;
        var peak = PeakOf(profile, bitrateBps);

        return new VideoEncoderSettings(
            source.Item1,
            source.Item2,
            output.Width,
            output.Height,
            frameRate ?? profile.FrameRate,
            mean,
            peak,
            profile.QualityVsSpeed);
    }

    public static uint PeakOf(QualityProfile profile, uint? bitrateBps)
    {
        var mean = bitrateBps ?? profile.MeanBitrateBps;
        return (uint)((long)mean * profile.PeakBitrateBps / profile.MeanBitrateBps);
    }
}
