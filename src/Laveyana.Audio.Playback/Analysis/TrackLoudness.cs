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

/// <summary>Two K-weighting biquads per channel, 400ms blocks with 100ms hop.
/// Short-term profile samples 3s blocks once per second. Only block powers are retained.</summary>
internal sealed class TrackLoudnessAnalyzer
{
    private const int Hop = 4800;
    private Biquad _ls = Biquad.Shelf(), _rs = Biquad.Shelf();
    private Biquad _lh = Biquad.HighPass(), _rh = Biquad.HighPass();
    private readonly double[] _history = new double[30];
    private readonly List<double> _blocks = new();
    private readonly List<double> _shortPowers = new();
    private readonly List<LoudnessPoint> _short = new();
    private int _count, _hops;
    private double _sum, _peak, _sum4, _sum30;
    private long _frames;

    public void Add(ReadOnlySpan<float> pcm)
    {
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            double l = float.IsFinite(pcm[i]) ? Math.Clamp(pcm[i], -1, 1) : 0;
            double r = float.IsFinite(pcm[i+1]) ? Math.Clamp(pcm[i+1], -1, 1) : 0;
            _peak = Math.Max(_peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            l = _lh.Process(_ls.Process(l));
            r = _rh.Process(_rs.Process(r));
            _sum += l*l + r*r;
            _frames++;
            if (++_count != Hop) continue;
            double power = _sum / Hop;
            int index = _hops % 30;
            _sum30 += power - _history[index];
            _sum4 += power - (_hops >= 4 ? _history[(_hops - 4) % 30] : 0);
            _history[index] = power;
            _hops++;
            if (_hops >= 4) _blocks.Add(Math.Max(0, _sum4 / 4));
            if (_hops >= 30 && _hops % 10 == 0 && _short.Count < 86400)
            {
                double shortPower = Math.Max(0, _sum30 / 30);
                _shortPowers.Add(shortPower);
                _short.Add(new(_frames, (float)Math.Max(-120, ToLufs(shortPower))));
            }
            _count = 0;
            _sum = 0;
        }
    }

    public TrackLoudness Finish()
    {
        double integratedThreshold = Threshold(_blocks, 10);
        double integratedSum = 0;
        int integratedCount = 0;
        foreach (double power in _blocks)
            if (power >= integratedThreshold) { integratedSum += power; integratedCount++; }
        double shortThreshold = Threshold(_shortPowers, 20);
        var shortGated = _shortPowers.Where(power => power >= shortThreshold).ToArray();
        Array.Sort(shortGated);
        float? range = shortGated.Length < 2 ? null :
            (float)Math.Max(0, ToLufs(Percentile(shortGated, .95)) - ToLufs(Percentile(shortGated, .10)));
        return new(integratedCount == 0 ? null : (float)ToLufs(integratedSum / integratedCount),
            _peak > 0 ? (float)(20 * Math.Log10(_peak)) : null, range, _short.ToArray());
    }

    private static double ToLufs(double power) => -.691 + 10 * Math.Log10(power);
    private static double Threshold(List<double> powers, int relative)
    {
        double absolute = Math.Pow(10, (-70 + .691) / 10);
        double sum = 0;
        int count = 0;
        foreach (double power in powers)
            if (power >= absolute) { sum += power; count++; }
        return count == 0 ? double.PositiveInfinity :
            Math.Max(absolute, sum / count * Math.Pow(10, -relative / 10d));
    }

    private static double Percentile(double[] sorted, double p)
    {
        double position = p * (sorted.Length - 1);
        int lo = (int)position, hi = Math.Min(lo + 1, sorted.Length - 1);
        // Interpolate in loudness (dB), not linear power.
        return Math.Exp(Math.Log(sorted[lo]) + (position - lo) *
            (Math.Log(sorted[hi]) - Math.Log(sorted[lo])));
    }

    // ITU-R BS.1770-5 Annex 1, Tables 1 and 2, 48 kHz.
    private struct Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1, _z2;
        public static Biquad Shelf() => new(1.53512485958697, -2.69169618940638, 1.19839281085285,
            -1.69065929318241, .73248077421585);
        public static Biquad HighPass() => new(1, -2, 1, -1.99004745483398, .99007225036621);
        public double Process(double x)
        {
            double y = b0*x + _z1;
            _z1 = b1*x - a1*y + _z2;
            _z2 = b2*x - a2*y;
            return y;
        }
    }
}
