namespace Laveyana.Audio.Playback;

/// <summary>Scores distinct low-frequency and mid/high-frequency attack proxies.
/// These are not separated instrument stems or verified bar downbeats.</summary>
internal static class RhythmHarmonicFamily
{
    internal static TrackRhythmHypothesis Evaluate(TrackRhythmHypothesis h,
        (int Index, double Weight)[] bass, (int Index, double Weight)[] percussion,
        float harmonicSupport)
    {
        var b = Measure(bass, h);
        var p = Measure(percussion, h);
        double eventF1 = .5 * (F1(b.Recall,b.Precision) + F1(p.Recall,p.Precision));
        double precision = .5 * (b.Precision + p.Precision);
        // Keep direct tempo confidence distinct. A family-level periodic peak
        // is useful for proposing candidates but must not manufacture certainty.
        double score = .25 * h.TempoConfidence + .25 * h.PhaseConfidence +
            .10 * h.Stability + .08 * h.AttackCoverage + .18 * eventF1 +
            .10 * precision + .04 * h.ReferenceAgreement;
        return h with { HarmonicSupport = harmonicSupport, FamilyScore = (float)Math.Clamp(score,0,1),
            BassAttackRecall = (float)b.Recall, BassBeatPrecision = (float)b.Precision,
            PercussionAttackRecall = (float)p.Recall, PercussionBeatPrecision = (float)p.Precision };
    }

    internal static bool SupportsExtra(TrackRhythmHypothesis h) =>
        h.Coverage >= .50f && h.AttackCoverage >= .25f &&
        Math.Max(h.BassBeatPrecision, h.PercussionBeatPrecision) >= .45f &&
        Math.Max(h.BassAttackRecall, h.PercussionAttackRecall) >= .30f;

    internal static (double Recall, double Precision) Measure(
        (int Index, double Weight)[] attacks, TrackRhythmHypothesis h)
    {
        if (attacks.Length == 0 || h.CandidateFrames.Length == 0) return default;
        long tolerance = (long)Math.Round(Math.Min(.14 * 60 * PlaybackFormat.SampleRate / h.Bpm,
            .09 * PlaybackFormat.SampleRate));
        double total = 0, matched = 0;
        foreach (var attack in attacks) total += attack.Weight;
        int i = 0, j = 0, count = 0;
        // One-to-one, chronological matching prevents one attack from
        // supporting several candidate beats. Weights measure missed accents.
        while (i < attacks.Length && j < h.CandidateFrames.Length)
        {
            long frame = h.StartFrame + (long)attacks[i].Index * RhythmSpectralFlux.Hop;
            long beat = h.CandidateFrames[j];
            if (frame < beat - tolerance) i++;
            else if (beat < frame - tolerance) j++;
            else { matched += attacks[i++].Weight; j++; count++; }
        }
        return (total > 0 ? matched / total : 0, count / (double)h.CandidateFrames.Length);
    }

    private static double F1(double recall, double precision) =>
        recall + precision > 0 ? 2 * recall * precision / (recall + precision) : 0;

    internal static TrackRhythmHypothesis? ReferenceCandidate(long[] beats, long start, long end,
        double root, double[] pulse, (int Index, double Weight)[] attacks,
        (int Index, double Weight)[] bass, (int Index, double Weight)[] percussion,
        Func<double,double> periodicity, CancellationToken token)
    {
        if (beats.Length < 4) return null;
        var gaps = new long[beats.Length-1];
        for (int i = 0; i < gaps.Length; i++) gaps[i] = beats[i+1] - beats[i];
        Array.Sort(gaps);
        double period = gaps[gaps.Length/2];
        if (period <= 0) return null;
        double bpm = 60d * PlaybackFormat.SampleRate / period;
        if (bpm < 40 || bpm > 240 || gaps.Any(g => Math.Abs(g-period) > .25*period)) return null;
        // Do not turn a few historical marks into an apparent full-block grid.
        if (beats[0] - start > 2*period || end - beats[^1] > 2*period) return null;
        double periodic = periodicity(bpm);
        if (periodic < .025) return null;
        double threshold = Math.Max(.16, pulse.Average() * 1.45);
        double radius = Math.Min(.17*period/RhythmSpectralFlux.Hop,3.5);
        int supported = 0;
        double error = 0;
        foreach (long frame in beats)
        {
            token.ThrowIfCancellationRequested();
            double position = (frame-start)/(double)RhythmSpectralFlux.Hop;
            double strongest = 0, nearest = position;
            for (int i = Math.Max(0,(int)Math.Ceiling(position-radius));
                i <= Math.Min(pulse.Length-1,(int)Math.Floor(position+radius)); i++)
            {
                double value = pulse[i]*(1-.22*Math.Abs(i-position)/radius);
                if (value > strongest) { strongest = value; nearest = i; }
            }
            if (strongest < threshold) continue;
            supported++; error += Math.Abs(nearest-position)*RhythmSpectralFlux.Hop/period;
        }
        double coverage = supported/(double)beats.Length;
        if (supported < 3 || coverage < .35) return null;
        double phase = coverage*Math.Clamp(1-3*error/supported,0,1);
        double stability = Math.Clamp(1-3*gaps.Average(g=>Math.Abs(g-period))/period,0,1);
        float tempo = (float)Math.Clamp(periodic*2,0,1);
        var h = new TrackRhythmHypothesis(start,end,bpm,root,tempo,(float)phase,
            (float)stability,(float)coverage,0,beats)
            { IsFamilyOnly = true, IsReferenceCandidate = true };
        double recall = Measure(attacks,h).Recall;
        // Same weak prior as other candidates; not an automatic winner merely
        // because it agrees with itself. Neutralize its self-agreement.
        h = h with { AttackCoverage = (float)recall, ReferenceAgreement = 0 };
        h = Evaluate(h,bass,percussion,tempo);
        return SupportsExtra(h) ? h : null;
    }
}
