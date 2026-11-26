namespace Laveyana.Audio.Playback;

public static partial class TrackRhythmAnalyzer
{
    // A partially successful long phase lock must not prevent local retries in
    // its uncovered regions. Only fully contained, independently supported,
    // stable-tempo windows are retried. Existing confirmed beats never move here.
    private static void RecoverUncoveredSections(List<TrackTempoSection> sections, List<long> beats,
        List<TrackBeatEstimate> predicted, double[] tempos, float[] quality, bool[] eligible,
        float[,] periodicity, IReadOnlyList<Onset> onsets, RhythmBandEvidence bands,
        int stride, int radius, double hopsPerSecond, int hopFrames, long totalFrames,
        CancellationToken token, TrackRhythmTrace? trace)
    {
        if (sections.Count == 0 || sections.Count >= 4096 || predicted.Count >= MaximumBeatFrames) return;
        var recovered = new List<TrackTempoSection>();
        var additions = new List<TrackBeatEstimate>();
        int window = 0;
        long gapStart = 0;
        long WindowStart(int w) => w == 0 ? 0 :
            Math.Max(0, ((long)w * stride - stride / 2) * hopFrames);
        long WindowEnd(int w) => w == tempos.Length - 1 ? totalFrames :
            Math.Min(totalFrames, ((long)w * stride + stride / 2) * hopFrames);
        bool Supported(int w)
        {
            int c = (int)Math.Round((tempos[w] - MinimumBpm) / BpmResolution);
            return eligible[w] && quality[w] >= .25f && c >= 0 && c < TempoCandidates &&
                periodicity[w, c] >= .14f;
        }

        for (int g = 0; g <= sections.Count; g++)
        {
            token.ThrowIfCancellationRequested();
            long gapEnd = g == sections.Count ? totalFrames : sections[g].StartFrame;
            while (window < tempos.Length && WindowStart(window) < gapStart) window++;
            while (window < tempos.Length && WindowEnd(window) <= gapEnd)
            {
                if (!Supported(window)) { window++; continue; }
                int first = window++;
                while (window < tempos.Length && WindowEnd(window) <= gapEnd && Supported(window) &&
                    window - first < 6 && Math.Abs(tempos[window] - tempos[first]) <= 3)
                    window++;
                if (window - first < 3) continue;
                var localSections = new List<TrackTempoSection>();
                var localBeats = new List<long>();
                var localPredictions = new List<TrackBeatEstimate>();
                int attempt = trace?.Attempts.Count ?? 0;
                AnalyzeSection(first, window, tempos, quality, onsets, stride, radius,
                    hopsPerSecond, hopFrames, totalFrames, localSections, localBeats, localPredictions,
                    bands, token, recoverLocalPhase: true, trace: trace);
                int accepted = 0;
                foreach (var section in localSections)
                {
                    if (section.StartFrame < gapStart || section.EndFrame > gapEnd ||
                        section.Confidence < .35f || recovered.Count + sections.Count >= 4096) continue;
                    var estimates = localPredictions.Where(e => e.Confirmed &&
                        e.Frame >= section.StartFrame && e.Frame < section.EndFrame).ToArray();
                    if (estimates.Length < 4 || estimates.Count(e => e.MatchedOnset && !e.EnergyBreak) <
                        Math.Ceiling(estimates.Length * .55) || estimates.Any(e => e.EnergyBreak) ||
                        predicted.Count + additions.Count + estimates.Length > MaximumBeatFrames) continue;
                    recovered.Add(section);
                    additions.AddRange(estimates);
                    accepted++;
                }
                if (trace is not null && trace.Attempts.Count > attempt)
                    trace.Attempts[attempt] = trace.Attempts[attempt] with
                    { Decision = accepted == 0 ? "uncovered_retry_rejected" : "uncovered_retry_accepted" };
            }
            if (g < sections.Count) gapStart = sections[g].EndFrame;
        }
        if (recovered.Count == 0) return;
        int region = 0;
        predicted.RemoveAll(e =>
        {
            while (region < recovered.Count && recovered[region].EndFrame <= e.Frame) region++;
            return region < recovered.Count && e.Frame >= recovered[region].StartFrame;
        });
        predicted.AddRange(additions);
        predicted.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        sections.AddRange(recovered);
        sections.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
        beats.Clear();
        beats.AddRange(predicted.Where(e => e.Confirmed).Select(e => e.Frame));
    }

    private sealed record SectionPhaseCandidate(TrackBeatEstimate[] Beats, double Score, double Shift);

    // Offline two-state/three-state path across compatible sections. A candidate
    // may only replace confirmed beats if it has comparable independent support.
    // Real phase resets remain possible because transition costs are bounded.
    private static void AlignSectionPhases(List<TrackTempoSection> sections, List<long> beats,
        List<TrackBeatEstimate> predicted, RhythmBandEvidence bands, double hopsPerSecond,
        int hopFrames, CancellationToken token, TrackRhythmTrace? trace)
    {
        if (sections.Count < 2) return;
        var states = new List<SectionPhaseCandidate>[sections.Count];
        var costs = new double[sections.Count][];
        var back = new int[sections.Count][];
        int cursor = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var section = sections[i];
            while (cursor < predicted.Count && predicted[cursor].Frame < section.StartFrame) cursor++;
            int from = cursor;
            while (cursor < predicted.Count && predicted[cursor].Frame < section.EndFrame) cursor++;
            var original = predicted.GetRange(from, cursor - from).Where(e => e.Confirmed).ToArray();
            if (original.Length < 4) return; // inconsistent source; retain existing grid
            double period = 60d * PlaybackFormat.SampleRate / section.Bpm;
            double radius = Math.Min(.10 * period / hopFrames, .065 * hopsPerSecond);
            double originalScore = original.Average(e => bands.FindPulse(e.Frame / (double)hopFrames, radius).Score);
            var options = new List<SectionPhaseCandidate> { new(original, originalScore, 0) };
            states[i] = options;
            // Do not reinterpret a clear fast kick grid. For a half-time
            // region require independent evidence of the doubled pulse.
            bool hasNeighbor = i > 0 && Compatible(sections[i - 1], section) ||
                i + 1 < sections.Count && Compatible(section, sections[i + 1]);
            if (section.Bpm <= 120 && hasNeighbor)
            {
                var (normal,doubled) = bands.MeasurePair((int)(section.StartFrame / hopFrames),
                    (int)Math.Ceiling(section.EndFrame / (double)hopFrames), section.Bpm, section.Bpm*2, token);
                if (normal >= .075 && doubled >= .10 && doubled >= .65 * normal)
                {
                    foreach (double shift in new[] { -.5, .5 })
                    {
                        var alternative = new List<TrackBeatEstimate>();
                        double score = 0;
                        int supported = 0, misses = 0, longest = 0;
                        foreach (var e in original)
                        {
                            double expected = (e.Frame + shift * period) / hopFrames;
                            if (expected * hopFrames < section.StartFrame || expected * hopFrames >= section.EndFrame)
                                continue;
                            var pulse = bands.FindPulse(expected, radius);
                            bool hit = pulse.Score >= .30;
                            bool trough = bands.IsEnergyBreak(expected) || bands.IsWeakOpening(expected);
                            if (trough) { alternative.Clear(); break; }
                            if (hit) { supported++; misses = 0; }
                            else longest = Math.Max(longest, ++misses);
                            int correction = hit ? (int)Math.Round((pulse.Hop - expected) * hopFrames * .45) : 0;
                            long frame = (long)Math.Round(expected * hopFrames) + correction;
                            if (frame < section.StartFrame || frame >= section.EndFrame ||
                                alternative.Count > 0 && frame <= alternative[^1].Frame) continue;
                            alternative.Add(new(frame, (float)Math.Clamp(.6 * pulse.Score + .4 * section.Confidence, 0, 1),
                                correction, hit, false, true));
                            score += pulse.Score;
                        }
                        if (alternative.Count < Math.Max(4, Math.Ceiling(original.Length * .80)) ||
                            supported < Math.Ceiling(alternative.Count * .55) || longest > 2) continue;
                        score /= alternative.Count;
                        if (score >= .25 && score >= originalScore - .03 &&
                            alternative.Count(e => e.MatchedOnset) >= Math.Ceiling(alternative.Count * .55))
                            options.Add(new(alternative.ToArray(), score, shift));
                    }
                }
            }
            costs[i] = new double[options.Count];
            back[i] = new int[options.Count];
            for (int c = 0; c < options.Count; c++)
            {
                double local = options[c].Score - (c == 0 ? 0 : .012);
                if (i == 0) { costs[i][c] = local; continue; }
                double best = double.NegativeInfinity;
                for (int prior = 0; prior < states[i - 1].Count; prior++)
                {
                    double transition = 0;
                    if (Compatible(sections[i - 1], section))
                    {
                        double spacing = 120d * PlaybackFormat.SampleRate / (sections[i - 1].Bpm + section.Bpm);
                        double intervals = (options[c].Beats[0].Frame - states[i - 1][prior].Beats[^1].Frame) / spacing;
                        double error = Math.Abs(intervals - Math.Round(intervals));
                        transition = .18 * error;
                    }
                    double candidate = costs[i - 1][prior] + local - transition;
                    if (candidate > best) { best = candidate; back[i][c] = prior; }
                }
                costs[i][c] = best;
            }
        }
        int selected = Array.IndexOf(costs[^1], costs[^1].Max());
        var replacements = new Dictionary<int, SectionPhaseCandidate>();
        for (int i = sections.Count - 1; i >= 0; i--)
        {
            var choice = states[i][selected];
            trace?.SectionPhasePath.Add(new(sections[i].StartFrame, sections[i].EndFrame,
                sections[i].Bpm, states[i].Count, states[i][0].Score, choice.Score, choice.Shift));
            if (selected != 0) replacements[i] = choice;
            selected = back[i][selected];
        }
        if (replacements.Count == 0) return;
        var rebuilt = new List<TrackBeatEstimate>(predicted.Count);
        cursor = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            while (cursor < predicted.Count && predicted[cursor].Frame < section.StartFrame)
                rebuilt.Add(predicted[cursor++]);
            if (replacements.TryGetValue(i, out var choice))
            {
                while (cursor < predicted.Count && predicted[cursor].Frame < section.EndFrame) cursor++;
                rebuilt.AddRange(choice.Beats);
                sections[i] = section with { MatchedBeats = choice.Beats.Count(e => e.MatchedOnset) };
            }
            else
                while (cursor < predicted.Count && predicted[cursor].Frame < section.EndFrame)
                    rebuilt.Add(predicted[cursor++]);
        }
        while (cursor < predicted.Count) rebuilt.Add(predicted[cursor++]);
        predicted.Clear();
        predicted.AddRange(rebuilt);
        beats.Clear();
        beats.AddRange(predicted.Where(e => e.Confirmed).Select(e => e.Frame));

        static bool Compatible(TrackTempoSection left, TrackTempoSection right) =>
            right.StartFrame >= left.EndFrame &&
            right.StartFrame - left.EndFrame <= 12L * PlaybackFormat.SampleRate &&
            Math.Abs(left.Bpm - right.Bpm) <= Math.Max(1.5, (left.Bpm + right.Bpm) * .0125);
    }

    // Extend only when at least three consecutive, independently supported
    // pulses agree with the preceding phase. Never extrapolate across silence.
    private static void ExtendSupportedTails(List<TrackTempoSection> sections, List<long> beats,
        List<TrackBeatEstimate> predicted, IReadOnlyList<Onset> onsets, RhythmBandEvidence bands,
        double hopsPerSecond, int hopFrames, long totalFrames, CancellationToken token)
    {
        var added = new List<TrackBeatEstimate>();
        var replaced = new List<(long Start, long End)>();
        for (int i = 0; i < sections.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var section = sections[i];
            long limit = Math.Min(totalFrames, i + 1 < sections.Count
                ? sections[i + 1].StartFrame : totalFrames);
            double period = 60d * PlaybackFormat.SampleRate / section.Bpm;
            int index = beats.BinarySearch(section.EndFrame);
            index = index < 0 ? ~index - 1 : index - 1;
            if (index < 0 || beats[index] < section.StartFrame) continue;
            long anchor = beats[index];
            var tail = new List<TrackBeatEstimate>();
            double radius = Math.Min(.14 * period / hopFrames, .075 * hopsPerSecond);
            for (int step = 1; step <= 8; step++)
            {
                double expected = (anchor + step * period) / hopFrames;
                if (expected * hopFrames >= limit || expected * hopFrames - anchor > 4 * PlaybackFormat.SampleRate ||
                    bands.IsEnergyBreak(expected) || bands.IsWeakOpening(expected)) break;
                var pulse = bands.FindPulse(expected, radius);
                if (pulse.Score < .35) break;
                long correction = (long)Math.Round((pulse.Hop - expected) * hopFrames * .45);
                long frame = (long)Math.Round(expected * hopFrames) + correction;
                if (frame >= limit) break;
                // A supported extrapolated beat can still lie inside the
                // previous section's trailing half-window. Continue probing
                // rather than rejecting the entire extension at this boundary.
                if (frame < section.EndFrame) continue;
                tail.Add(new(frame, (float)Math.Clamp(.6 * pulse.Score + .4 * section.Confidence, 0, 1),
                    (int)correction, true, false, true));
            }
            if (tail.Count < 3 || predicted.Count + added.Count + tail.Count > MaximumBeatFrames) continue;
            double q = bands.MeasurePair((int)(anchor / hopFrames),
                (int)Math.Ceiling(tail[^1].Frame / (double)hopFrames), section.Bpm, 0, token).First;
            if (q < .15) continue;
            long end = Math.Min(limit, tail[^1].Frame + (long)(period / 2) + 1);
            replaced.Add((section.EndFrame, end));
            added.AddRange(tail);
            sections[i] = section with { EndFrame = end, MatchedBeats = section.MatchedBeats + tail.Count,
                OnsetCount = LowerBound(onsets, end / (double)hopFrames) -
                    LowerBound(onsets, section.StartFrame / (double)hopFrames) };
        }
        if (added.Count == 0) return;
        RemoveReplacedPredictions(predicted, replaced, includeStart: true);
        predicted.AddRange(added);
        predicted.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        beats.Clear();
        beats.AddRange(predicted.Where(e => e.Confirmed).Select(e => e.Frame));
    }

    private static void RemoveReplacedPredictions(List<TrackBeatEstimate> predicted,
        List<(long Start, long End)> ranges, bool includeStart)
    {
        // Ranges are generated in chronological order, but callers may have
        // unsorted predictions. Binary search does not depend on their order.
        predicted.RemoveAll(e =>
        {
            int lo = 0, hi = ranges.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (ranges[mid].End <= e.Frame) lo = mid + 1;
                else hi = mid;
            }
            return lo < ranges.Count && (includeStart ? e.Frame >= ranges[lo].Start : e.Frame > ranges[lo].Start);
        });
    }

    // Bridge only bounded gaps between already confirmed, compatible regions.
    // Independent periodicity and onsets inside the gap are mandatory.
    private static void RecoverCompatibleGaps(List<TrackTempoSection> sections, List<long> beats,
        List<TrackBeatEstimate> predicted, IReadOnlyList<Onset> onsets, RhythmBandEvidence bands,
        double hopsPerSecond, int hopFrames, CancellationToken token, TrackRhythmTrace? trace)
    {
        if (sections.Count < 2) return;
        var merged = new List<TrackTempoSection>(sections.Count);
        var added = new List<TrackBeatEstimate>();
        var replaced = new List<(long Start, long End)>();
        merged.Add(sections[0]);
        for (int i = 1; i < sections.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var left = merged[^1];
            var right = sections[i];
            double bpm = (left.Bpm + right.Bpm) / 2;
            double period = 60d * PlaybackFormat.SampleRate / bpm;
            long gap = right.StartFrame - left.EndFrame;
            int li = beats.BinarySearch(left.EndFrame);
            li = li < 0 ? ~li - 1 : li - 1;
            int ri = beats.BinarySearch(right.StartFrame);
            if (ri < 0) ri = ~ri;
            if (gap < 0 || gap > 12L * PlaybackFormat.SampleRate ||
                Math.Abs(left.Bpm - right.Bpm) > Math.Max(1.5, bpm * .025) ||
                li < 0 || ri >= beats.Count || ri <= li)
            { merged.Add(right); continue; }
            long first = beats[li], last = beats[ri];
            double distance = last - first;
            int steps = (int)Math.Round(distance / period);
            double error = Math.Abs(distance / period - steps);
            if (steps < 2 || steps > 48 || error > .38 ||
                (error > .24 && error / steps > .06) ||
                distance / PlaybackFormat.SampleRate > 14 ||
                predicted.Count + added.Count + steps > MaximumBeatFrames)
            {
                trace?.BridgeAttempts.Add(new(first, last, bpm, error, 0, 0, 0, false, "phase_or_length"));
                merged.Add(right); continue;
            }
            double interval = distance / steps;
            int from = (int)Math.Clamp(first / hopFrames, 0, int.MaxValue);
            int to = (int)Math.Clamp(last / hopFrames + 1, 0, int.MaxValue);
            double correlation = bands.MeasurePair(from, to, bpm, 0, token).First;
            var bridge = new List<TrackBeatEstimate>(steps - 1);
            int supported = 0, consecutiveMisses = 0, maxMisses = 0, troughs = 0;
            for (int step = 1; step < steps; step++)
            {
                double expected = (first + step * interval) / hopFrames;
                double radius = Math.Min(.19 * interval / hopFrames, .09 * hopsPerSecond);
                double best = 0, nearest = expected;
                for (int k = LowerBound(onsets, expected - radius); k < onsets.Count &&
                    onsets[k].Index <= expected + radius; k++)
                {
                    var onset = onsets[k];
                    double value = onset.Strength * (1 - Math.Abs(onset.Index - expected) / radius);
                    if (value > best) { best = value; nearest = onset.Index; }
                }
                bool hit = best >= .16;
                bool trough = bands.IsEnergyBreak(expected) || bands.IsWeakOpening(expected);
                if (trough) troughs++;
                if (hit && !trough) { supported++; consecutiveMisses = 0; }
                else maxMisses = Math.Max(maxMisses, ++consecutiveMisses);
                long correction = hit ? (long)Math.Round((nearest - expected) * hopFrames * .4) : 0;
                long frame = (long)Math.Round(expected * hopFrames) + correction;
                bridge.Add(new(frame, (float)Math.Clamp(.40 * correlation + .60 * Math.Min(1, best / .6), 0, 1),
                    (int)correction, hit, trough, true));
            }
            bool relaxedPhase = error > .24;
            bool accepted = troughs == 0 && correlation >= (relaxedPhase ? .14 : .10) &&
                supported >= Math.Max(1, Math.Ceiling(bridge.Count * (relaxedPhase ? .70 : .55))) &&
                maxMisses <= (relaxedPhase ? 1 : 2);
            trace?.BridgeAttempts.Add(new(first, last, bpm, error, correlation, bridge.Count, supported,
                accepted, accepted ? "accepted" : "insufficient_local_evidence"));
            if (!accepted) { merged.Add(right); continue; }
            replaced.Add((first, last));
            added.AddRange(bridge);
            float confidence = Math.Min(left.Confidence, right.Confidence);
            merged[^1] = new(left.StartFrame, right.EndFrame, bpm, confidence,
                LowerBound(onsets, right.EndFrame / (double)hopFrames) -
                LowerBound(onsets, left.StartFrame / (double)hopFrames),
                left.MatchedBeats + right.MatchedBeats + supported);
        }
        if (added.Count == 0) return;
        // The original predictions in the gap must not coexist with its replacement.
        RemoveReplacedPredictions(predicted, replaced, includeStart: false);
        predicted.AddRange(added);
        predicted.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        beats.Clear();
        beats.AddRange(predicted.Where(e => e.Confirmed).Select(e => e.Frame));
        sections.Clear();
        sections.AddRange(merged);
    }
}
