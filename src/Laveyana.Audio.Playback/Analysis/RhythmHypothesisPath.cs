namespace Laveyana.Audio.Playback;

/// <summary>Globally selects a coherent sequence of unconfirmed hypotheses.
/// Never bridges silence or manufactures missing beat positions.</summary>
internal static class RhythmHypothesisPath
{
    internal static TrackRhythmHypothesis[] Select(IReadOnlyList<TrackRhythmHypothesis> source,
        CancellationToken token) => SelectCore(source, token, family: false);

    internal static TrackRhythmHypothesis[] SelectFamily(IReadOnlyList<TrackRhythmHypothesis> source,
        CancellationToken token) => SelectCore(source, token, family: true);

    private static TrackRhythmHypothesis[] SelectCore(IReadOnlyList<TrackRhythmHypothesis> source,
        CancellationToken token, bool family)
    {
        if (source.Count == 0) return [];
        var output = source.Select(h => family ? h with { IsFamilyPreferred = false } :
            h with { IsPreferred = false }).ToArray();
        bool Preferred(TrackRhythmHypothesis h) => family ? h.IsFamilyPreferred : h.IsPreferred;
        TrackRhythmHypothesis Mark(TrackRhythmHypothesis h, bool selected) =>
            family ? h with { IsFamilyPreferred = selected } : h with { IsPreferred = selected };
        double Transition(TrackRhythmHypothesis a, TrackRhythmHypothesis b) =>
            Cost(a, b, family);
        double EmissionScore(TrackRhythmHypothesis h) =>
            (family ? h.FamilyScore : h.Score) - (h.IsRecoveryCandidate ? .08 : 0);
        var blocks = Enumerable.Range(0, output.Length)
            .GroupBy(i => output[i].StartFrame).Select(g => g.ToArray()).ToArray();
        bool Touch(int left, int right) =>
            output[blocks[right][0]].StartFrame - output[blocks[left][0]].EndFrame
                <= RhythmSpectralFlux.Hop;
        bool Close(double a, double b) => Math.Abs(a - b) <= .04 * Math.Max(a, b);
        bool Strong(int block) => blocks[block].Any(i => !output[i].IsRecoveryCandidate);
        var eligible = new int[blocks.Length][];
        for (int b = 0; b < blocks.Length; b++)
        {
            token.ThrowIfCancellationRequested();
            int index = b;
            eligible[b] = blocks[b].Where(i =>
            {
                var h = output[i];
                if (!h.IsRecoveryCandidate) return true;
                // Snapshot eligibility prevents chained recovery.
                if (index == 0 || index + 1 == blocks.Length || !Strong(index-1) || !Strong(index+1) ||
                    !Touch(index-1,index) || !Touch(index,index+1) || h.Coverage < .50f ||
                    h.AttackCoverage < .25f) return false;
                return blocks[index-1].Any(j => !output[j].IsRecoveryCandidate && Close(output[j].Bpm,h.Bpm) &&
                    Transition(output[j],h) < .12) &&
                    blocks[index+1].Any(j => !output[j].IsRecoveryCandidate && Close(output[j].Bpm,h.Bpm) &&
                    Transition(h,output[j]) < .12);
            }).ToArray();
        }

        // Viterbi over bounded candidate lists, reset on actual gaps.
        for (int start = 0; start < blocks.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (eligible[start].Length == 0) { start++; continue; }
            int end = start + 1;
            while (end < blocks.Length && eligible[end].Length > 0 && Touch(end-1,end)) end++;
            var back = new int[end-start][];
            double[] previous = eligible[start].Select(i => EmissionScore(output[i])).ToArray();
            for (int b = start + 1; b < end; b++)
            {
                token.ThrowIfCancellationRequested();
                var current = new double[eligible[b].Length];
                var pointers = back[b-start] = new int[current.Length];
                for (int j = 0; j < current.Length; j++)
                {
                    var h = output[eligible[b][j]];
                    double best = double.NegativeInfinity;
                    for (int k = 0; k < previous.Length; k++)
                    {
                        double value = previous[k] - Transition(output[eligible[b-1][k]],h);
                        if (value > best) { best = value; pointers[j] = k; }
                    }
                    current[j] = best + EmissionScore(h);
                }
                previous = current;
            }
            int winner = Array.IndexOf(previous, previous.Max());
            for (int b = end - 1; b >= start; b--)
            {
                int i = eligible[b][winner];
                output[i] = Mark(output[i], true);
                if (b > start) winner = back[b-start][winner];
            }
            start = end;
        }
        // Even if compatible candidates existed, the selected neighbors must
        // themselves support the recovery. Do not publish an isolated weak winner.
        for (int b = 0; b < blocks.Length; b++)
        {
            int i = blocks[b].FirstOrDefault(j => Preferred(output[j]) && output[j].IsRecoveryCandidate, -1);
            if (i < 0) continue;
            int left = b > 0 ? blocks[b-1].FirstOrDefault(j => Preferred(output[j]), -1) : -1;
            int right = b+1 < blocks.Length ? blocks[b+1].FirstOrDefault(j => Preferred(output[j]), -1) : -1;
            if (left < 0 || right < 0 || !Close(output[left].Bpm,output[i].Bpm) ||
                !Close(output[right].Bpm,output[i].Bpm) || Transition(output[left],output[i]) >= .12 ||
                Transition(output[i],output[right]) >= .12)
                output[i] = Mark(output[i], false);
        }
        return output;
    }

    internal static double Cost(TrackRhythmHypothesis a, TrackRhythmHypothesis b, bool family = false)
    {
        double ratio = Math.Max(a.Bpm,b.Bpm) / Math.Min(a.Bpm,b.Bpm);
        double tempo = Math.Min(.28, .60 * Math.Abs(Math.Log2(ratio)));
        if (family) tempo *= .55 + .45 * Math.Min(a.Coverage, b.Coverage);
        double period = 60d * PlaybackFormat.SampleRate / Math.Max(a.Bpm,b.Bpm);
        double cycles = (b.CandidateFrames[0] - (double)a.CandidateFrames[^1]) / period;
        double phase = Math.Abs(cycles - Math.Round(cycles));
        return tempo + (ratio <= 1.08 || Math.Abs(ratio - 2) <= .08 ? .24 * phase : 0);
    }
}
