namespace Laveyana.Audio.Playback;

/// <summary>Independent band periodicity on a bounded-rate positive energy flux.</summary>
internal sealed class RhythmBandEvidence
{
    private readonly double _hopsPerSecond;
    private readonly int _bin;
    private readonly float[][] _flux;
    private readonly double[] _bassPrefix;
    private readonly double[] _energyPrefix;

    public RhythmBandEvidence(IReadOnlyList<RhythmEnergy> energy, double hopsPerSecond, CancellationToken token)
    {
        _hopsPerSecond = hopsPerSecond;
        _bin = Math.Max(1, (int)Math.Round(hopsPerSecond / 50));
        int count = (energy.Count - 1) / _bin + 1;
        _flux = [new float[count], new float[count], new float[count]];
        _bassPrefix = new double[energy.Count + 1];
        _energyPrefix = new double[energy.Count + 1];
        for (int i = 0; i < energy.Count; i++)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            var now = energy[i];
            _bassPrefix[i + 1] = _bassPrefix[i] + Math.Max(0, now.Low);
            _energyPrefix[i + 1] = _energyPrefix[i] + Math.Max(0, now.Low) + Math.Max(0, now.Mid) + Math.Max(0, now.High);
            if (i == 0) continue;
            var before = energy[i - 1];
            int index = i / _bin;
            _flux[0][index] += Rise(now.Low, before.Low);
            _flux[1][index] += Rise(now.Mid, before.Mid);
            _flux[2][index] += Rise(now.High, before.High);
        }
        foreach (var band in _flux)
            for (int i = 0; i < band.Length; i++) band[i] = MathF.Sqrt(band[i]);
    }

    private static float Rise(float current, float previous) =>
        Math.Max(0, MathF.Sqrt(Math.Max(0, current)) - MathF.Sqrt(Math.Max(0, previous)));

    public Periodicity Measure(int firstHop, int endHop)
    {
        int start = Math.Clamp(firstHop / _bin, 0, _flux[0].Length);
        int end = Math.Clamp((int)Math.Ceiling(endHop / (double)_bin), start, _flux[0].Length);
        double rate = _hopsPerSecond / _bin;
        int maxLag = Math.Min(end - start - 1, (int)Math.Ceiling(rate * 60 / 40) + 1);
        if (maxLag < 1) return new([], rate);
        var maximum = new double[maxLag + 1];
        var sum = new double[maxLag + 1];
        foreach (float[] band in _flux)
        {
            double mean = 0;
            for (int i = start; i < end; i++) mean += band[i];
            mean /= end - start;
            double norm = 0;
            for (int i = start; i < end; i++)
            {
                double value = band[i] - mean;
                norm += value * value;
            }
            if (norm <= 1e-12) continue;
            for (int lag = 1; lag <= maxLag; lag++)
            {
                double dot = 0;
                for (int i = start; i < end - lag; i++)
                    dot += (band[i] - mean) * (band[i + lag] - mean);
                double value = Math.Clamp(dot / norm, 0, 1);
                sum[lag] += value;
                maximum[lag] = Math.Max(maximum[lag], value);
            }
        }
        for (int lag = 1; lag <= maxLag; lag++)
            maximum[lag] = .55 * maximum[lag] + .15 * sum[lag];
        return new(maximum, rate);
    }

    /// <summary>A simultaneous relative loss of bass and overall energy, not an absolute loudness cutoff.</summary>
    public bool IsEnergyBreak(double hop)
    {
        int center = (int)Math.Round(hop);
        int shortRadius = Math.Max(1, (int)Math.Round(.7 * _hopsPerSecond));
        int side = Math.Max(1, (int)Math.Round(6 * _hopsPerSecond));
        int width = Math.Max(1, (int)Math.Round(2 * _hopsPerSecond));
        double bass = Mean(_bassPrefix, center - shortRadius, center + shortRadius + 1);
        double total = Mean(_energyPrefix, center - shortRadius, center + shortRadius + 1);
        double bassReference = Math.Max(Mean(_bassPrefix, center - side - width, center - side + width),
            Mean(_bassPrefix, center + side - width, center + side + width));
        double energyReference = Math.Max(Mean(_energyPrefix, center - side - width, center - side + width),
            Mean(_energyPrefix, center + side - width, center + side + width));
        return bassReference > 1e-9 && energyReference > 1e-9 &&
            bass < .42 * bassReference && total < .70 * energyReference;
    }

    private static double Mean(double[] prefix, int from, int to)
    {
        from = Math.Clamp(from, 0, prefix.Length - 1);
        to = Math.Clamp(to, from, prefix.Length - 1);
        return to > from ? (prefix[to] - prefix[from]) / (to - from) : 0;
    }

    internal readonly record struct Periodicity(double[] Values, double Rate)
    {
        public double AtBpm(double bpm)
        {
            double lag = Rate * 60 / bpm;
            int lower = (int)lag;
            if (lower < 1 || lower + 1 >= Values.Length) return 0;
            return Values[lower] + (Values[lower + 1] - Values[lower]) * (lag - lower);
        }
    }
}
