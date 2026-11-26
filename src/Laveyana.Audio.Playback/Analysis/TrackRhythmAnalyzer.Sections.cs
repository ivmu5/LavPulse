namespace Laveyana.Audio.Playback;

public static partial class TrackRhythmAnalyzer
{
    // Preserve every accepted original region. Only retry rejected regions in
    // short independent blocks, using stronger confidence and the same support gate.
    private static void AnalyzeSectionWithRecovery(int firstWindow, int endWindow,
        double[] tempos, float[] quality, IReadOnlyList<Onset> onsets, int stride, int radius,
        double hopsPerSecond, int hopFrames, long totalFrames,
        List<TrackTempoSection> sections, List<long> beats, List<TrackBeatEstimate> predicted, RhythmBandEvidence bands,
        CancellationToken token, TrackRhythmTrace? trace)
    {
        int predictionStart = predicted.Count;
        if (AnalyzeSection(firstWindow, endWindow, tempos, quality, onsets, stride, radius,
            hopsPerSecond, hopFrames, totalFrames, sections, beats, predicted, bands, token, trace: trace))
            return;
        if (endWindow - firstWindow < 4) return;
        predicted.RemoveRange(predictionStart, predicted.Count - predictionStart);

        const int blockWindows = 6; // approximately 12 seconds at the 2-second stride
        for (int start = firstWindow; start < endWindow;)
        {
            token.ThrowIfCancellationRequested();
            int end = Math.Min(start + blockWindows, endWindow);
            if (endWindow - end < 3) end = endWindow; // avoid an unsupported tiny tail
            if (end - start >= 3)
                AnalyzeSection(start, end, tempos, quality, onsets, stride, radius,
                    hopsPerSecond, hopFrames, totalFrames, sections, beats, predicted, bands, token,
                    recoverLocalPhase: true, trace: trace);
            start = end;
        }
    }

    private static bool AnalyzeSection(int firstWindow, int endWindow, double[] tempos, float[] quality,
        IReadOnlyList<Onset> onsets, int stride, int radius, double hopsPerSecond,
        int hopFrames, long totalFrames, List<TrackTempoSection> sections, List<long> beats,
        List<TrackBeatEstimate> predicted, RhythmBandEvidence bands, CancellationToken token, bool recoverLocalPhase = false, TrackRhythmTrace? trace = null)
    {
        double meanConfidence = 0;
        var localTempos = new List<double>();
        for (int i = firstWindow; i < endWindow; i++)
        {
            meanConfidence += quality[i];
            localTempos.Add(tempos[i]);
        }
        meanConfidence /= endWindow - firstWindow;
        localTempos.Sort();
        double bpm = localTempos[localTempos.Count / 2];
        if (meanConfidence < .18)
        {
            trace?.Attempts.Add(new(firstWindow, endWindow, recoverLocalPhase, meanConfidence, bpm,
                0, 0, 0, 0, 0, 0, false, "insufficient_mean_confidence"));
            return false;
        }
        // Anchor phase close to the FIRST valid window. Starting from the middle of a long
        // accelerando with the median tempo introduces a cumulative phase error at its start.
        double anchorBpm = recoverLocalPhase ? bpm : tempos[firstWindow];
        double period = hopsPerSecond * 60 / anchorBpm;
        double phaseSpeed = Math.Tau / period;
        int anchorWindow = recoverLocalPhase ? firstWindow + (endWindow - firstWindow - 1) / 2 : firstWindow;
        int center = anchorWindow * stride;
        double allReal = 0, allImaginary = 0, strongReal = 0, strongImaginary = 0;
        double strongTotal = 0, strongSquares = 0, broadTotal = 0;
        int anchorStart = LowerBound(onsets, center - radius);
        for (int i = anchorStart; i < onsets.Count && onsets[i].Index <= (long)center + radius; i++)
        {
            Onset onset = onsets[i];
            (double sine, double cosine) = Math.SinCos(onset.Index * phaseSpeed);
            allReal += onset.Strength * cosine;
            allImaginary += onset.Strength * sine;
            broadTotal += onset.Strength;
            double weight = BeatWeight(onset);
            strongReal += weight * cosine;
            strongImaginary += weight * sine;
            strongTotal += weight;
            strongSquares += weight * weight;
        }
        double strongCoherence = strongTotal > 0 ? Math.Sqrt(strongReal * strongReal + strongImaginary * strongImaginary) / strongTotal : 0;
        double broadCoherence = broadTotal > 0 ? Math.Sqrt(allReal * allReal + allImaginary * allImaginary) / broadTotal : 0;
        bool useStrong = strongSquares > 0 && strongTotal * strongTotal / strongSquares >= 5 &&
            strongCoherence >= .30 && strongCoherence >= .85 * broadCoherence;
        double real = useStrong ? strongReal : allReal;
        double imaginary = useStrong ? strongImaginary : allImaginary;
        double phase = (Math.Atan2(imaginary, real) + Math.Tau) % Math.Tau;
        double phaseInHops = phase / Math.Tau * period;
        long startFrame = WindowBoundary(firstWindow,stride,hopFrames,totalFrames);
        long endFrame = endWindow == tempos.Length ? totalFrames :
            WindowBoundary(endWindow,stride,hopFrames,totalFrames);
        if (startFrame >= endFrame)
        {
            trace?.Attempts.Add(new(firstWindow, endWindow, recoverLocalPhase, meanConfidence, bpm,
                anchorBpm, phaseInHops, startFrame, endFrame, 0, 0, useStrong, "empty_frame_range"));
            return false;
        }

        double fromHop = startFrame / (double)hopFrames;
        double toHop = endFrame / (double)hopFrames;
        // Try both parities of a supported subdivision plus nearby phase offsets.
        // Keep the original phase for strongly coherent kick patterns.
        var phaseChoice = bands.ChoosePhase(phaseInHops, period, anchorBpm,
            Math.Max(fromHop, center - radius), Math.Min(toHop, center + radius),
            useStrong && strongCoherence >= .65, token);
        phaseInHops = phaseChoice.Phase;
        bool alternatePhase = phaseChoice.Alternate;
        if (alternatePhase) useStrong = false;
        trace?.PhaseAttempts.Add(new(firstWindow, endWindow, anchorBpm, phaseChoice.OriginalPhase,
            phaseChoice.Phase, phaseChoice.OriginalScore, phaseChoice.SelectedScore,
            phaseChoice.Subdivision, alternatePhase));
        double position = phaseInHops + Math.Ceiling((fromHop - phaseInHops) / period) * period;
        int onsetCursor = LowerBound(onsets, fromHop - period);

        var localBeats = new List<long>();
        var supports = new List<bool>();
        var breaks = new List<bool>();
        var beatEstimates = new List<TrackBeatEstimate>();
        double nextPhaseCheck = fromHop;
        int matched = 0, missed = 0;
        while (position * hopFrames < endFrame && localBeats.Count < MaximumBeatFrames - predicted.Count)
        {
            if ((localBeats.Count & 1023) == 0) token.ThrowIfCancellationRequested();
            double localBpm = TempoAt(position, tempos, stride, firstWindow, endWindow);
            double spacing = 60 * hopsPerSecond / localBpm;
            // Re-estimate local phase without restarting the whole grid. Limit
            // corrections to a fraction of one beat to preserve pulse continuity.
            double phaseCorrection = 0;
            if (position >= nextPhaseCheck)
            {
                phaseCorrection = alternatePhase
                    ? bands.CorrectPhase(position, spacing, fromHop, toHop)
                    : LocalPhaseCorrection(onsets, position, spacing, hopsPerSecond,
                        useStrong, missed, fromHop, toHop);
                position = Math.Max(fromHop, position + phaseCorrection);
                nextPhaseCheck = position + 2 * hopsPerSecond;
                if (position * hopFrames >= endFrame) break;
            }
            double searchRadius = spacing * (missed >= 3 ? .28 : .19);
            while (onsetCursor < onsets.Count && onsets[onsetCursor].Index < position - searchRadius)
                onsetCursor++;

            double strongest = 0, nearest = position;
            for (int i = onsetCursor; i < onsets.Count && onsets[i].Index <= position + searchRadius; i++)
            {
                double distance = Math.Abs(onsets[i].Index - position);
                // Lock to the same prominent percussion used to determine the phase.
                // Otherwise a nearby hi-hat can pull the grid off a real kick.
                double weight = useStrong
                    ? Math.Max(Math.Clamp((onsets[i].Accent - .76f) * 4, 0, 1.5),
                        .55 * onsets[i].Strength)
                    : onsets[i].Strength;
                double evidence = weight * (1 - distance / searchRadius);
                if (evidence <= strongest) continue;
                strongest = evidence;
                nearest = onsets[i].Index;
            }
            if (alternatePhase)
            {
                // Use the same multiband evidence that selected this parity.
                // A louder kick on the opposing parity must not pull it back.
                var pulse = bands.FindPulse(position, searchRadius);
                strongest = pulse.Score;
                nearest = pulse.Hop;
            }
            double correction = 0;
            bool hit = strongest >= (alternatePhase ? .30 : useStrong ? .22 : .13);
            if (hit)
            {
                correction = Math.Clamp((nearest - position) * (missed >= 3 ? .70 : .55),
                    -spacing * .2, spacing * .2);
                matched++;
                missed = 0;
            }
            else missed++;

            long frame = Math.Clamp((long)Math.Round((position + correction) * hopFrames), startFrame, endFrame - 1);
            if ((localBeats.Count == 0 || frame > localBeats[^1]) &&
                (predicted.Count == 0 || frame > predicted[^1].Frame))
            {
                localBeats.Add(frame);
                supports.Add(hit);
                bool energyBreak = bands.IsEnergyBreak(position) && strongest < .5;
                // A quiet fade-in may contain a periodic melody long before
                // a comparable rhythmic attack becomes audible. Preserve its
                // prediction, but do not confirm that weak opening.
                energyBreak |= bands.IsWeakOpening(position);
                breaks.Add(energyBreak);
                int window = Math.Clamp((int)Math.Round(position / stride), firstWindow, endWindow - 1);
                float beatConfidence = (float)Math.Clamp(.55 * Math.Min(1, strongest / .55) +
                    .45 * quality[window], 0, 1);
                if (energyBreak) beatConfidence *= .25f;
                beatEstimates.Add(new(frame, beatConfidence,
                    (int)Math.Clamp(Math.Round((phaseCorrection + correction) * hopFrames), int.MinValue, int.MaxValue),
                    hit, energyBreak, false));
            }
            position += spacing + correction * .6;
        }
        // Validate local runs, not the average of an entire song. Predictions
        // remain separate from the confirmed public grid.
        double minimumSupport = alternatePhase ? .45 : useStrong ? .43 : .35;
        int publishedBefore = beats.Count;
        long halfPeriod = Math.Max(1, (long)Math.Round(30 * PlaybackFormat.SampleRate / bpm));
        int runStart = 0;
        void PublishRun(int start, int end)
        {
            // Do not let a look-ahead tempo window publish a leading/trailing
            // run of wholly unsupported beats before the first or after the last attack.
            while (start < end && (!supports[start] || breaks[start])) start++;
            while (end > start && (!supports[end - 1] || breaks[end - 1])) end--;
            if (sections.Count >= 4096) return;
            if (sections.Count > 0)
                while (start < end && localBeats[start] < sections[^1].EndFrame) start++;
            while (start < end && (!supports[start] || breaks[start])) start++;
            if (end - start < 4) return;
            int supportedCount = 0;
            for (int i = start; i < end; i++)
                if (supports[i] && !breaks[i]) supportedCount++;
            if (supportedCount < Math.Max(3, (int)Math.Ceiling((end - start) * minimumSupport))) return;
            long from = Math.Max(startFrame, localBeats[start] - halfPeriod);
            long to = Math.Min(endFrame, localBeats[end - 1] + halfPeriod + 1);
            if (sections.Count > 0) from = Math.Max(from, sections[^1].EndFrame);
            if (from >= to) return;
            int count = LowerBound(onsets, to / (double)hopFrames) -
                LowerBound(onsets, from / (double)hopFrames);
            float confidence = (float)Math.Clamp(.78 * meanConfidence + .22 * supportedCount / (end - start), 0, 1);
            sections.Add(new(from, to, bpm, confidence, count, supportedCount));
            for (int i = start; i < end; i++)
            {
                beats.Add(localBeats[i]);
                beatEstimates[i] = beatEstimates[i] with { Confirmed = true };
            }
        }
        for (int i = 0; i < localBeats.Count;)
        {
            if (supports[i] && !breaks[i]) { i++; continue; }
            int gapStart = i;
            int energyBreaks = 0;
            while (i < localBeats.Count && (!supports[i] || breaks[i]))
            {
                if (breaks[i]) energyBreaks++;
                i++;
            }
            int length = i - gapStart;
            // Suppress sustained missing percussion or a distinct energy trough;
            // preserve isolated syncopated/missing beats.
            if (length >= Math.Max(3, (int)Math.Ceiling(bpm / 40)) || energyBreaks >= 2)
            {
                PublishRun(runStart, gapStart);
                runStart = i;
            }
        }
        PublishRun(runStart, localBeats.Count);
        bool supported = beats.Count > publishedBefore;
        predicted.AddRange(beatEstimates);
        trace?.Attempts.Add(new(firstWindow, endWindow, recoverLocalPhase, meanConfidence, bpm,
            anchorBpm, phaseInHops, startFrame, endFrame, localBeats.Count, matched, useStrong,
            supported ? (beats.Count - publishedBefore < localBeats.Count ? "accepted_with_gaps" : "accepted")
                : "insufficient_beat_support"));
        return supported;
    }
}
