namespace Laveyana.Audio.Playback;

/// <summary>Independent band periodicity on a bounded-rate positive energy flux.</summary>
internal sealed class RhythmBandEvidence
{
    private readonly double _hopsPerSecond;
    private readonly int _bin;
    private readonly float[][] _flux;
    private readonly double[] _bassPrefix;
    private readonly double[] _energyPrefix;
    private readonly double[][] _fluxPrefix;

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
        _fluxPrefix = new double[3][];
        for (int band = 0; band < 3; band++)
        {
            _fluxPrefix[band] = new double[count + 1];
            for (int i = 0; i < count; i++)
            {
                _flux[band][i] = MathF.Sqrt(_flux[band][i]);
                _fluxPrefix[band][i + 1] = _fluxPrefix[band][i] + _flux[band][i];
            }
        }
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

    internal readonly record struct PhaseChoice(double OriginalPhase, double Phase,
        double OriginalScore, double SelectedScore, bool Subdivision, bool Alternate);

    // Independent, locally normalized multiband pulse evidence. The high band
    // participates in ambiguous half-time selection, not in visual kick detection.
    private double PulseAt(double hop)
    {
        int index = (int)Math.Round(hop / _bin);
        if (index < 0 || index >= _flux[0].Length) return 0;
        int span = Math.Max(2, (int)Math.Round(_hopsPerSecond * 2 / _bin));
        double value = 0;
        for (int b = 0; b < 3; b++)
        {
            double mean = Mean(_fluxPrefix[b], index - span, index + span + 1);
            double flux = _flux[b][index];
            double normalized = flux / (flux + 3 * mean + 1e-9);
            value += normalized * (b == 0 ? .27 : b == 1 ? .35 : .38);
        }
        return value;
    }

    public (double Hop, double Score) FindPulse(double hop, double radius)
    {
        int start = Math.Max(0, (int)Math.Ceiling((hop - radius) / _bin));
        int end = Math.Min(_flux[0].Length - 1, (int)Math.Floor((hop + radius) / _bin));
        double best = 0, selected = hop;
        for (int i = start; i <= end; i++)
        {
            double candidate = i * _bin;
            double score = PulseAt(candidate) * (1 - .35 * Math.Abs(candidate - hop) / Math.Max(1, radius));
            if (score > best) { best = score; selected = candidate; }
        }
        return (selected, best);
    }

    private double PhaseScore(double phase, double period, double start, double end)
    {
        double sum = 0, squares = 0;
        int count = 0;
        double radius = Math.Min(.12 * period, .065 * _hopsPerSecond);
        for (double hop = phase + Math.Ceiling((start - phase) / period) * period; hop < end; hop += period)
        {
            double pulse = FindPulse(hop, radius).Score;
            sum += pulse;
            squares += pulse * pulse;
            count++;
        }
        if (count < 4) return 0;
        double mean = sum / count;
        double deviation = Math.Sqrt(Math.Max(0, squares / count - mean * mean));
        return mean - .22 * deviation;
    }

    public PhaseChoice ChoosePhase(double phase, double period, double bpm, double start,
        double end, bool strongAnchor)
    {
        if (end - start < 4 * period || bpm > 120 || strongAnchor)
            return new(phase, phase, 0, 0, false, false);
        var evidence = Measure((int)Math.Floor(start), (int)Math.Ceiling(end));
        double normal = evidence.AtBpm(bpm), doubled = evidence.AtBpm(bpm * 2);
        if (normal < .075 || doubled < .10 || doubled < .65 * normal)
            return new(phase, phase, 0, 0, false, false);
        double original = PhaseScore(phase, period, start, end);
        double best = original, selected = phase;
        // Two parity families, with limited refinement around each.
        for (int parity = 0; parity < 2; parity++)
            for (int offset = -2; offset <= 2; offset++)
            {
                double candidate = phase + (parity * .5 + offset * .0625) * period;
                double score = PhaseScore(candidate, period, start, end);
                if (score > best) { best = score; selected = candidate; }
            }
        double difference = Math.Abs(selected - phase);
        bool alternate = difference >= .30 * period && best >= original + .025 && best >= .25;
        return new(phase, alternate ? (selected % period + period) % period : phase,
            original, best, true, alternate);
    }

    public double CorrectPhase(double position, double spacing, double start, double end)
    {
        double first = Math.Max(start, position - 2 * _hopsPerSecond);
        double last = Math.Min(end, position + 2 * _hopsPerSecond);
        double original = PhaseScore(position, spacing, first, last);
        double best = original, delta = 0;
        for (int i = -2; i <= 2; i++)
        {
            double shift = i * .05 * spacing;
            double score = PhaseScore(position + shift, spacing, first, last);
            if (score > best + .015) { best = score; delta = shift; }
        }
        return delta * .6;
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
