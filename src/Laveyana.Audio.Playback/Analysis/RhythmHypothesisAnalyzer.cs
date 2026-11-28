namespace Laveyana.Audio.Playback;

/// <summary>Independent, bounded local spectral pulse hypotheses. These are
/// deliberately not confirmed playback beats or a verified metrical interpretation.</summary>
internal static class RhythmHypothesisAnalyzer
{
    private const int Block = 600, MinimumBlock = 250, MaxHypotheses = 4096, MaxBeats = 32768;
    private const double Rate = (double)PlaybackFormat.SampleRate / RhythmSpectralFlux.Hop;
    private static readonly double[] Weights = [.24, .25, .19, .17, .15];

    internal static TrackRhythmHypothesis[] Analyze(IReadOnlyList<RhythmSpectrumFrame> frames,
        TrackBeatGrid reference, long totalFrames, CancellationToken token)
    {
        var result = new List<TrackRhythmHypothesis>();
        int totalBeats = 0, window = 0;
        for (int start = 0; start + MinimumBlock <= frames.Count; start += Block)
        {
            token.ThrowIfCancellationRequested();
            int end = Math.Min(start + Block, frames.Count);
            int length = end - start;
            var bands = new double[5, length];
            var pulse = new double[length];
            var correlations = new double[76 + 1];
            double activity = 0;
            for (int b = 0; b < 5; b++)
            {
                var prefix = new double[length + 1];
                for (int i = 0; i < length; i++)
                {
                    double value = Math.Max(0, frames[start + i].Band(b));
                    prefix[i + 1] = prefix[i] + value;
                }
                activity += prefix[length] / length;
                double mean = 0;
                for (int i = 0; i < length; i++)
                {
                    int lo = Math.Max(0, i - 50), hi = Math.Min(length, i + 51);
                    double localMean = (prefix[hi] - prefix[lo]) / (hi - lo);
                    double value = frames[start + i].Band(b);
                    double normalized = value / (value + 2 * localMean + 1e-8);
                    bands[b,i] = normalized;
                    pulse[i] += Weights[b] * normalized;
                    mean += normalized / length;
                }
                double norm = 0;
                for (int i = 0; i < length; i++) norm += Math.Pow(bands[b,i] - mean, 2);
                if (norm < 1e-9) continue;
                for (int lag = 12; lag < correlations.Length; lag++)
                {
                    double dot = 0;
                    for (int i = 0; i < length - lag; i++)
                        dot += (bands[b,i] - mean) * (bands[b,i + lag] - mean);
                    correlations[lag] += Weights[b] * Math.Clamp(dot / norm, 0, 1);
                }
            }
            if (activity < 1e-7) continue;
            double Periodicity(double bpm)
            {
                double lag = 60 * Rate / bpm;
                // Linear lag interpolation puts every maximum on an integer
                // 20 ms hop. A quadratic neighborhood recovers fractional
                // periods (e.g. 172 BPM is 17.44 hops, not 17 or 18).
                int i = Math.Clamp((int)Math.Floor(lag + .5), 13, 75);
                double x = lag - i;
                double previous = correlations[i - 1], center = correlations[i], next = correlations[i + 1];
                return Math.Clamp(center + .5 * (next - previous) * x +
                    .5 * (previous - 2 * center + next) * x * x, 0, 1);
            }
            long firstFrame = frames[start].Frame;
            long lastFrame = Math.Min(totalFrames, frames[end - 1].Frame + RhythmSpectralFlux.Hop);
            long middle = firstFrame + (lastFrame - firstFrame) / 2;
            while (window + 1 < reference.Windows.Length &&
                Mid(reference.Windows[window + 1]) <= middle) window++;
            double root = reference.Windows.Length == 0 ? reference.Bpm : reference.Windows[window].Bpm;
            if (!double.IsFinite(root) || root < 40 || root > 240) root = 0;
            // Local spectral maximum, plus the existing tempo family. Refine
            // each related candidate independently within a narrow neighborhood.
            double strongest = 40, bestPeriodic = 0;
            for (double bpm = 40; bpm <= 240; bpm += .5)
            {
                double p = Periodicity(bpm);
                if (p > bestPeriodic) { bestPeriodic = p; strongest = bpm; }
            }
            if (bestPeriodic < .035) continue;
            bool weakBlock = bestPeriodic < .075;
            double Refine(double bpm)
            {
                double best = bpm, score = Periodicity(bpm);
                for (double v = Math.Max(40, bpm * .985); v <= Math.Min(240, bpm * 1.015); v += .25)
                    if (Periodicity(v) > score) { score = Periodicity(v); best = v; }
                return best;
            }
            var candidates = new List<double>(4);
            void Add(double bpm)
            {
                if (bpm < 40 || bpm > 240) return;
                bpm = Refine(bpm);
                if (candidates.All(c => Math.Abs(c - bpm) > .025 * bpm)) candidates.Add(bpm);
            }
            Add(strongest);
            if (root > 0) { Add(root); Add(root * 2); Add(root / 2); }
            Add(strongest * 2); Add(strongest / 2);
            var local = new List<TrackRhythmHypothesis>(12);
            var attacks = StrongAttacks(pulse);
            var referenceBeats = ReferenceBeats(reference.BeatFrames, firstFrame, lastFrame);
            foreach (double bpm in candidates)
            {
                token.ThrowIfCancellationRequested();
                double periodic = Periodicity(bpm);
                if (periodic < (weakBlock ? .035 : .045)) continue;
                foreach (var hypothesis in Build(pulse, bpm, periodic, firstFrame, lastFrame,
                    root, attacks, referenceBeats, token))
                    local.Add(hypothesis with { IsRecoveryCandidate = weakBlock });
            }
            if (local.Count == 0) continue;
            // Score is a relative ranking, never a calibrated probability.
            var ordered = local.OrderByDescending(h => h.Score).ToArray();
            // Keep complete blocks instead of truncating competing phases.
            if (result.Count + ordered.Length > MaxHypotheses ||
                totalBeats + ordered.Sum(h => h.CandidateFrames.Length) > MaxBeats) break;
            for (int i = 0; i < ordered.Length; i++)
            {
                var h = ordered[i];
                result.Add(h with { IsLocalPreferred = i == 0 });
                totalBeats += h.CandidateFrames.Length;
            }
        }
        return RhythmHypothesisPath.Select(result, token);
    }

    private static long Mid(TrackTempoWindow window) =>
        window.StartFrame + (window.EndFrame - window.StartFrame) / 2;

    private static TrackRhythmHypothesis[] Build(double[] pulse, double bpm, double periodic,
        long start, long end, double root, (int Index, double Weight)[] attacks,
        long[] referenceBeats, CancellationToken token)
    {
        double period = 60 * Rate / bpm, radius = Math.Min(.17 * period, .07 * Rate);
        double mean = pulse.Average();
        double threshold = Math.Max(.16, mean * 1.45);
        (double Position, double Value) Peak(double t)
        {
            double best = 0, pos = t;
            for (int i = Math.Max(0, (int)Math.Ceiling(t - radius));
                i <= Math.Min(pulse.Length - 1, (int)Math.Floor(t + radius)); i++)
            {
                double value = pulse[i] * (1 - .22 * Math.Abs(i - t) / radius);
                if (value > best) { best = value; pos = i; }
            }
            return (pos, best);
        }
        var phaseScores = new double[24];
        for (int p = 0; p < 24; p++)
        {
            double candidate = p * period / 24, phaseScore = 0, off = 0;
            int count = 0;
            for (double t = candidate; t < pulse.Length; t += period)
            {
                phaseScore += Peak(t).Value;
                off += Peak(t + period / 2).Value;
                count++;
            }
            phaseScores[p] = (phaseScore - .30 * off) / Math.Max(1, count);
        }
        int bestPhase = Array.IndexOf(phaseScores, phaseScores.Max());
        var phases = new List<int> { bestPhase };
        int Distance(int a, int b) => Math.Min(Math.Abs(a - b), 24 - Math.Abs(a - b));
        int opposite = (bestPhase + 12) % 24;
        phases.Add(Enumerable.Range(0, 24).Where(p => Distance(p, opposite) <= 3)
            .OrderByDescending(p => phaseScores[p]).First());
        foreach (int p in Enumerable.Range(0, 24).OrderByDescending(p => phaseScores[p]))
        {
            if (phases.All(q => Distance(p, q) >= 5) &&
                phaseScores[p] >= phaseScores[bestPhase] * .65)
            { phases.Add(p); break; }
        }
        var hypotheses = new List<TrackRhythmHypothesis>(phases.Count);
        foreach (int phaseIndex in phases)
        {
            token.ThrowIfCancellationRequested();
            if (BuildPhase(phaseIndex * period / 24) is { } h) hypotheses.Add(h);
        }
        return hypotheses.ToArray();

        TrackRhythmHypothesis? BuildPhase(double phase)
        {
            var beats = new List<long>();
            int supported = 0, countAll = 0, intervals = 0;
            double error = 0, intervalError = 0, last = double.NaN;
            // Fixed local lattice plus a bounded, measured phase correction.
            // Never integrate onset corrections into the period indefinitely.
            // The correction is recomputed from nearby pulses every ~2 seconds.
            double shift = 0, nextCheck = 0;
            for (double t = phase; t < pulse.Length; t += period)
            {
                token.ThrowIfCancellationRequested();
                if (t >= nextCheck)
                {
                    double best = double.NegativeInfinity, selected = shift;
                    for (int j = -4; j <= 4; j++)
                    {
                        double candidate = j * .05 * period, shiftScore = 0;
                        int n = 0;
                        for (int k = -3; k <= 3; k++)
                        {
                            double probe = t + k * period + candidate;
                            if (probe < 0 || probe >= pulse.Length) continue;
                            shiftScore += Peak(probe).Value; n++;
                        }
                        if (n >= 4 && shiftScore / n > best) { best = shiftScore / n; selected = candidate; }
                    }
                    shift = Math.Clamp(selected, shift - .10 * period, shift + .10 * period);
                    nextCheck = t + 2 * Rate;
                }
                double position = t + shift;
                if (position < 0 || position >= pulse.Length) continue;
                var peak = Peak(position);
                bool hit = peak.Value >= threshold;
                if (hit)
                {
                    supported++;
                    error += Math.Abs(peak.Position - position) / period;
                    position += .5 * (peak.Position - position);
                }
                countAll++;
                long frame = start + (long)Math.Round(position * RhythmSpectralFlux.Hop);
                if (frame < start || frame >= end || beats.Count > 0 && frame <= beats[^1]) continue;
                if (double.IsFinite(last)) { intervalError += Math.Abs(position - last - period) / period; intervals++; }
                last = position;
                beats.Add(frame);
            }
            if (beats.Count < 4 || supported < 3 || countAll == 0) return null;
            double coverage = supported / (double)countAll;
            if (coverage < .35) return null;
            double phaseConfidence = coverage * Math.Clamp(1 - 3 * error / supported, 0, 1);
            double stability = Math.Clamp(1 - 3 * intervalError / Math.Max(1, intervals), 0, 1);
            double tempoConfidence = Math.Clamp(periodic * 2, 0, 1);
            var positions = beats.ToArray();
            long tolerance = (long)Math.Round(Math.Min(.14 * period, .09 * Rate) * RhythmSpectralFlux.Hop);
            double attackTotal = 0, attackMatched = 0;
            foreach (var attack in attacks)
            {
                attackTotal += attack.Weight;
                if (NearestDistance(positions, start + (long)attack.Index * RhythmSpectralFlux.Hop) <= tolerance)
                    attackMatched += attack.Weight;
            }
            double attackCoverage = attackTotal > 0 ? attackMatched / attackTotal : 0;
            int referenceMatched = 0;
            foreach (long frame in referenceBeats)
                if (NearestDistance(positions, frame) <= tolerance) referenceMatched++;
            double agreement = referenceBeats.Length >= 4 ? referenceMatched / (double)referenceBeats.Length : 0;
            // Attack recall counters sparse bar aliases; lattice support penalizes
            // excessive subdivisions. Reference alignment is only a weak prior.
            double score = .30 * tempoConfidence + .30 * phaseConfidence + .14 * stability +
                .22 * attackCoverage + .04 * agreement;
            return new(start, end, bpm, root, (float)tempoConfidence, (float)phaseConfidence,
                (float)stability, (float)coverage, (float)score, positions)
            {
                AttackCoverage = (float)attackCoverage, ReferenceAgreement = (float)agreement,
                PhaseOffset = (float)(phase / period)
            };
        }
    }

    private static (int Index, double Weight)[] StrongAttacks(double[] pulse)
    {
        double threshold = Math.Max(.19, pulse.Average() * 1.65);
        var attacks = new List<(int Index, double Weight)>();
        for (int i = 1; i + 1 < pulse.Length; i++)
        {
            if (pulse[i] < threshold || pulse[i] < pulse[i-1] || pulse[i] <= pulse[i+1]) continue;
            double weight = pulse[i] * pulse[i];
            if (attacks.Count > 0 && i - attacks[^1].Index < 5)
            {
                if (weight > attacks[^1].Weight) attacks[^1] = (i, weight);
            }
            else attacks.Add((i, weight));
        }
        return attacks.ToArray();
    }

    private static long[] ReferenceBeats(long[] beats, long start, long end)
    {
        int first = Array.BinarySearch(beats, start);
        if (first < 0) first = ~first;
        int last = Array.BinarySearch(beats, end);
        if (last < 0) last = ~last;
        return beats[first..last];
    }

    private static long NearestDistance(long[] beats, long frame)
    {
        int i = Array.BinarySearch(beats, frame);
        if (i >= 0) return 0;
        i = ~i;
        return Math.Min(i > 0 ? frame - beats[i-1] : long.MaxValue,
            i < beats.Length ? beats[i] - frame : long.MaxValue);
    }

    internal static bool IsValid(TrackRhythmHypothesis[]? hypotheses, long total)
    {
        if (hypotheses is null || hypotheses.Length > MaxHypotheses) return false;
        int beats = 0;
        foreach (var h in hypotheses)
        {
            if (h is null || h.StartFrame < 0 || h.EndFrame <= h.StartFrame || h.EndFrame > total ||
                !double.IsFinite(h.Bpm) || h.Bpm < 40 || h.Bpm > 240 ||
                !double.IsFinite(h.ReferenceBpm) || h.ReferenceBpm < 0 || h.ReferenceBpm > 240 ||
                !Unit(h.TempoConfidence) || !Unit(h.PhaseConfidence) || !Unit(h.Stability) ||
                !Unit(h.Coverage) || !Unit(h.Score) || !Unit(h.AttackCoverage) ||
                !Unit(h.ReferenceAgreement) || !Unit(h.PhaseOffset) || h.CandidateFrames is not { Length: >= 4 and <= MaxBeats })
                return false;
            beats += h.CandidateFrames.Length;
            if (beats > MaxBeats) return false;
            long previous = h.StartFrame - 1;
            foreach (long frame in h.CandidateFrames)
            {
                if (frame <= previous || frame >= h.EndFrame) return false;
                previous = frame;
            }
        }
        return true;
        static bool Unit(float v) => float.IsFinite(v) && v >= 0 && v <= 1;
    }
}
