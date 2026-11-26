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
    private readonly double _openingReference;
    private readonly double[] _pulse;

    public RhythmBandEvidence(IReadOnlyList<RhythmEnergy> energy, double hopsPerSecond, CancellationToken token)
    {
        _hopsPerSecond = hopsPerSecond;
        _bin = Math.Max(1, (int)Math.Round(hopsPerSecond / 50));
        int count = (energy.Count - 1) / _bin + 1;
        _flux = [new float[count], new float[count], new float[count]];
        _bassPrefix = new double[energy.Count + 1];
        _energyPrefix = new double[energy.Count + 1];
        float previousLow=0, previousMid=0, previousHigh=0;
        for (int i = 0; i < energy.Count; i++)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            var now = energy[i];
            _bassPrefix[i + 1] = _bassPrefix[i] + Math.Max(0, now.Low);
            _energyPrefix[i + 1] = _energyPrefix[i] + Math.Max(0, now.Low) + Math.Max(0, now.Mid) + Math.Max(0, now.High);
            float low=MathF.Sqrt(Math.Max(0,now.Low)), mid=MathF.Sqrt(Math.Max(0,now.Mid)),
                high=MathF.Sqrt(Math.Max(0,now.High));
            if (i>0)
            {
                int index=i/_bin;
                _flux[0][index]+=Math.Max(0,low-previousLow);
                _flux[1][index]+=Math.Max(0,mid-previousMid);
                _flux[2][index]+=Math.Max(0,high-previousHigh);
            }
            previousLow=low; previousMid=mid; previousHigh=high;
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
        // Every candidate phase reuses this immutable normalized pulse field.
        // Previously each FindPulse recalculated three local means per probe.
        _pulse = new double[count];
        int span = Math.Max(2, (int)Math.Round(_hopsPerSecond * 2 / _bin));
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            for (int b = 0; b < 3; b++)
            {
                double mean = Mean(_fluxPrefix[b], i - span, i + span + 1);
                double flux = _flux[b][i];
                double normalized = flux / (flux + 3 * mean + 1e-9);
                _pulse[i] += normalized * (b == 0 ? .27 : b == 1 ? .35 : .38);
            }
        }
        // Reference is a median of later two-second low/mid flux means.
        // Relative gating preserves consistently quiet music.
        int block = Math.Max(1, (int)Math.Round(2 * hopsPerSecond / _bin));
        var levels = new List<double>();
        for (int from = 3 * block; from + block <= Math.Min(count, 16 * block); from += block)
            levels.Add(Mean(_fluxPrefix[0], from, from + block) +
                .7 * Mean(_fluxPrefix[1], from, from + block));
        levels.Sort();
        _openingReference = levels.Count >= 4 ? levels[levels.Count / 2] : 0;
    }

    public bool IsWeakOpening(double hop)
    {
        if (hop < 0 || hop > 12 * _hopsPerSecond || _openingReference <= 1e-9) return false;
        int center = (int)Math.Round(hop / _bin);
        int radius = Math.Max(2, (int)Math.Round(.8 * _hopsPerSecond / _bin));
        double current = Mean(_fluxPrefix[0], center - radius, center + radius + 1) +
            .7 * Mean(_fluxPrefix[1], center - radius, center + radius + 1);
        return current < .28 * _openingReference;
    }

    public Periodicity Measure(int firstHop, int endHop, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
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
                token.ThrowIfCancellationRequested();
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

    // Recovery needs one or two tempos, not the entire lag spectrum.
    // Use the same centering, normalization and interpolation as Measure.
    internal (double First, double Second) MeasurePair(int firstHop, int endHop,
        double firstBpm, double secondBpm, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        int start = Math.Clamp(firstHop / _bin, 0, _flux[0].Length);
        int end = Math.Clamp((int)Math.Ceiling(endHop / (double)_bin), start, _flux[0].Length);
        double rate = _hopsPerSecond / _bin;
        int maxLag = Math.Min(end-start-1, (int)Math.Ceiling(rate*60/40)+1);
        double first = rate*60/firstBpm, second = rate*60/secondBpm;
        bool Valid(double lag) => double.IsFinite(lag) && lag >= 1 && lag < maxLag;
        bool validFirst = Valid(first), validSecond = Valid(second);
        if (!validFirst && !validSecond) return default;
        Span<int> lags = stackalloc int[4];
        lags[0] = validFirst ? (int)first : 0;
        lags[1] = validFirst ? lags[0]+1 : 0;
        lags[2] = validSecond ? (int)second : 0;
        lags[3] = validSecond ? lags[2]+1 : 0;
        Span<double> maximum = stackalloc double[4], sum = stackalloc double[4];
        maximum.Clear(); sum.Clear();
        foreach (float[] band in _flux)
        {
            token.ThrowIfCancellationRequested();
            double mean = 0;
            for (int i=start;i<end;i++) mean+=band[i];
            mean/=end-start;
            double norm=0;
            for (int i=start;i<end;i++)
            {
                double value=band[i]-mean;
                norm+=value*value;
            }
            if (norm<=1e-12) continue;
            for (int j=0;j<lags.Length;j++)
            {
                int lag=lags[j];
                if (lag==0) continue;
                token.ThrowIfCancellationRequested();
                double dot=0;
                for (int i=start;i<end-lag;i++)
                    dot+=(band[i]-mean)*(band[i+lag]-mean);
                double value=Math.Clamp(dot/norm,0,1);
                sum[j]+=value;
                maximum[j]=Math.Max(maximum[j],value);
            }
        }
        for(int j=0;j<4;j++) maximum[j]=.55*maximum[j]+.15*sum[j];
        return (validFirst ? maximum[0]+(maximum[1]-maximum[0])*(first-lags[0]) : 0,
            validSecond ? maximum[2]+(maximum[3]-maximum[2])*(second-lags[2]) : 0);
    }

    internal readonly record struct PhaseChoice(double OriginalPhase, double Phase,
        double OriginalScore, double SelectedScore, bool Subdivision, bool Alternate);

    // Independent, locally normalized multiband pulse evidence. The high band
    // participates in ambiguous half-time selection, not in visual kick detection.
    public (double Hop, double Score) FindPulse(double hop, double radius)
    {
        int start = Math.Max(0, (int)Math.Ceiling((hop - radius) / _bin));
        int end = Math.Min(_flux[0].Length - 1, (int)Math.Floor((hop + radius) / _bin));
        double best = 0, selected = hop;
        for (int i = start; i <= end; i++)
        {
            double candidate = i * _bin;
            double score = _pulse[i] * (1 - .35 * Math.Abs(candidate - hop) / Math.Max(1, radius));
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
        double end, bool strongAnchor, CancellationToken token = default)
    {
        if (end - start < 4 * period || bpm > 120 || strongAnchor)
            return new(phase, phase, 0, 0, false, false);
        var (normal,doubled) = MeasurePair((int)Math.Floor(start), (int)Math.Ceiling(end), bpm, bpm*2, token);
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
            if (!double.IsFinite(lag) || lag < 1 || lag >= Values.Length - 1) return 0;
            int lower = (int)lag;
            if (lower < 1 || lower + 1 >= Values.Length) return 0;
            return Values[lower] + (Values[lower + 1] - Values[lower]) * (lag - lower);
        }
    }
}
