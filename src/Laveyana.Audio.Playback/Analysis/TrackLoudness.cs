namespace Laveyana.Audio.Playback;

public sealed record LoudnessPoint(long EndFrame, float Lufs);

/// <summary>48 kHz stereo BS.1770-style loudness. Sample peak is NOT true peak.</summary>
public sealed record TrackLoudness(float? IntegratedLufs, float? SamplePeakDbfs, float? LoudnessRange,
    LoudnessPoint[] ShortTerm)
{
    public const int Version = 1;
    public float? RecommendedGainDb(float targetLufs = -14)
    {
        if (IntegratedLufs is not float integrated || SamplePeakDbfs is not float peak ||
            !float.IsFinite(integrated) || !float.IsFinite(peak) || !float.IsFinite(targetLufs)) return null;
        // At most 6 dB boost and 3 dB sample-peak headroom. Not a limiter
        // or an inter-sample peak guarantee; opt-in playback uses this gain.
        return Math.Min(Math.Clamp(targetLufs - integrated, -30, 6), -3 - peak);
    }

    public bool IsValid(long frames) =>
        Valid(IntegratedLufs, -70, 20) && Valid(SamplePeakDbfs, -1000, 20) &&
        Valid(LoudnessRange, 0, 100) && ShortTerm is { Length: <= 86400 } &&
        OrderedShortTerm(frames);

    private bool OrderedShortTerm(long frames)
    {
        long previous = 0;
        foreach (var point in ShortTerm)
        {
            if (point is null || point.EndFrame <= previous || point.EndFrame > frames ||
                !float.IsFinite(point.Lufs) || point.Lufs is < -120 or > 20) return false;
            previous = point.EndFrame;
        }
        return true;
    }

    private static bool Valid(float? value, float low, float high) =>
        value is null || float.IsFinite(value.Value) && value.Value >= low && value.Value <= high;
}
