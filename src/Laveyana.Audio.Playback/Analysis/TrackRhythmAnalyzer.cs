namespace Laveyana.Audio.Playback;

/// <summary>
/// Detects 10 ms percussive attacks, estimates tempo in short overlapping windows, smooths
/// tempo changes and locks each beat to nearby measured onsets. All work is off the UI thread.
/// </summary>
public static partial class TrackRhythmAnalyzer
{
    private const double MinimumBpm = 40;
    private const double MaximumBpm = 240;
    private const double BpmResolution = .5;
    private const int TempoCandidates = (int)((MaximumBpm - MinimumBpm) / BpmResolution) + 1;
    private const int MaximumStoredTransients = 8192;
    private const int MaximumBeatFrames = 32768;

    // Tempo estimation needs permissive onset candidates, but the visual hit markers
    // should represent only prominent low/mid attacks.
    private readonly record struct Onset(int Index, float Strength, float Accent);

    // Rich electronic productions contain many melody/hat/subdivision attacks per kick.
    // Uniformly weighting every onset destroys the phase coherence of the actual beat.
    // The positive-part squared accent focuses the tempo estimate on prominent bass/mid
    // attacks, while retaining the unfiltered score as secondary corroboration.
    private static double BeatWeight(Onset onset)
    {
        double accent = Math.Max(0, onset.Accent - .80f);
        return accent * accent;
    }

    public static bool IsValid(TrackBeatGrid rhythm, long totalFrames) =>
        rhythm is not null && totalFrames > 0 &&
        rhythm.Bpm >= 0 && rhythm.Bpm <= MaximumBpm && double.IsFinite(rhythm.Bpm) &&
        rhythm.Confidence >= 0 && rhythm.Confidence <= 1 && float.IsFinite(rhythm.Confidence) &&
        rhythm.FirstBeatFrame >= 0 && rhythm.FirstBeatFrame <= totalFrames &&
        rhythm.TransientFrames is { Length: <= MaximumStoredTransients } onsets &&
        rhythm.BeatFrames is { Length: <= MaximumBeatFrames } beats &&
        (beats.Length == 0 || (beats.Length >= 4 && beats[0] == rhythm.FirstBeatFrame)) &&
        IsAscending(onsets, totalFrames) && IsAscending(beats, totalFrames) &&
        rhythm.PredictedBeatFrames is { Length: <= MaximumBeatFrames } predicted && IsAscending(predicted, totalFrames) &&
        rhythm.BeatEstimates is { Length: <= MaximumBeatFrames } estimates &&
        estimates.All(estimate => estimate.Frame >= 0 && estimate.Frame < totalFrames &&
            float.IsFinite(estimate.Confidence) && estimate.Confidence >= 0 && estimate.Confidence <= 1) &&
        (estimates.Length == 0 || estimates.Length == predicted.Length &&
            estimates.Select(estimate => estimate.Frame).SequenceEqual(predicted) &&
            estimates.Where(estimate => estimate.Confirmed).Select(estimate => estimate.Frame).SequenceEqual(beats)) &&
        rhythm.Sections is { Length: <= 4096 } sections &&
        rhythm.Windows is { Length: <= 8192 } windows &&
        sections.All(section => section is not null && section.StartFrame >= 0 && section.StartFrame < section.EndFrame &&
            section.EndFrame <= totalFrames && section.Bpm >= MinimumBpm &&
            section.Bpm <= MaximumBpm && double.IsFinite(section.Bpm) &&
            float.IsFinite(section.Confidence) && section.Confidence >= 0 && section.Confidence <= 1 &&
            section.OnsetCount >= 0 && section.MatchedBeats >= 0 &&
            section.SubdivisionFactor is >= 1 and <= 4) &&
        windows.All(window => window is not null && window.StartFrame >= 0 && window.StartFrame <= window.EndFrame &&
            window.EndFrame <= totalFrames && window.Bpm >= 0 && window.Bpm <= MaximumBpm &&
            double.IsFinite(window.Bpm) && float.IsFinite(window.Confidence) &&
            window.Confidence >= 0 && window.Confidence <= 1 && window.OnsetCount >= 0 &&
            window.SubdivisionFactor is >= 1 and <= 4) &&
        SectionsContainBeats(sections, beats) &&
        rhythm.CandidateOnsets >= 0 &&
        rhythm.OnsetDiagnostics is { Length: <= MaximumStoredTransients } diagnostics &&
        diagnostics.All(hit => hit.Frame >= 0 && hit.Frame <= totalFrames &&
            float.IsFinite(hit.Strength) && float.IsFinite(hit.Accent) &&
            hit.Strength >= 0 && hit.Accent >= 0);

    private static bool SectionsContainBeats(TrackTempoSection[] sections, long[] beats)
    {
        if (sections.Length == 0) return true; // legacy/manual grids
        int cursor = 0;
        long previousEnd = 0;
        foreach (var section in sections)
        {
            if (section.StartFrame < previousEnd) return false;
            previousEnd = section.EndFrame;
            if (cursor < beats.Length && beats[cursor] < section.StartFrame) return false;
            int first = cursor;
            while (cursor < beats.Length && beats[cursor] < section.EndFrame) cursor++;
            if (cursor - first < 4 || section.MatchedBeats > cursor - first) return false;
        }
        return cursor == beats.Length;
    }

    private static bool IsAscending(long[] values, long limit)
    {
        long previous = -1;
        foreach (long value in values)
        {
            if (value <= previous || value > limit) return false;
            previous = value;
        }
        return true;
    }

    /// <summary>
    /// Supports synthetic/test waveforms and manually provided analysis. Regular MP3 scanning
    /// uses the much finer, independent 100 Hz PCM energy envelope instead.
    /// </summary>
    public static TrackBeatGrid Analyze(TrackWaveform waveform)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        var energy = waveform.Points.Select(p => new RhythmEnergy(p.Low, p.Mid, p.High)).ToArray();
        return AnalyzeEnergy(energy, waveform.FramesPerPoint, waveform.TotalFrames);
    }

    internal static TrackBeatGrid AnalyzeEnergy(IReadOnlyList<RhythmEnergy> energy, int framesPerHop,
        long totalFrames, CancellationToken cancellationToken = default, TrackRhythmTrace? trace = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (trace is not null)
        {
            trace.HopFrames = framesPerHop;
            trace.Energy = energy;
            trace.Onsets = [];
            trace.TempoScores = null;
            trace.RawTempoScores = null;
            trace.PhaseScores = null;
            trace.ConfidenceScores = null;
            trace.PeriodicityScores = null;
            trace.MinimumBpm = MinimumBpm;
            trace.BpmResolution = BpmResolution;
            trace.Attempts.Clear();
            trace.PhaseAttempts.Clear();
            trace.BridgeAttempts.Clear();
            trace.SectionPhasePath.Clear();
            trace.MetricRoot = null;
        }
        if (framesPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerHop));
        if (totalFrames <= 0 || energy.Count < 16)
            return Empty() with { AnalysisStatus = "Недостаточно аудиоданных для анализа" };
        double hopsPerSecond = PlaybackFormat.SampleRate / (double)framesPerHop;
        var onsets = DetectOnsets(energy, hopsPerSecond, cancellationToken);
        // Without reliable rhythm, show only exceptionally strong isolated accents.
        // Relax the visual threshold only after the grid is independently validated.
        long[] transients = StoreTransients(onsets, framesPerHop, totalFrames, .85f);
        TrackOnsetDiagnostic[] onsetDebug = SampleCandidates(onsets, framesPerHop, totalFrames);
        if (trace is not null)
            trace.Onsets = onsets.Select(hit => new TrackOnsetDiagnostic(
                Math.Min(totalFrames - 1, (long)hit.Index * framesPerHop), hit.Strength, hit.Accent)).ToArray();
        if (onsets.Count < 8)
            return Empty(transients) with
            {
                CandidateOnsets = onsets.Count,
                OnsetDiagnostics = onsetDebug,
                AnalysisStatus = "Недостаточно кандидатов ударов"
            };

        int stride = Math.Max(1, (int)Math.Round(2 * hopsPerSecond));
        int radius = Math.Max(8, (int)Math.Round(4.5 * hopsPerSecond));
        int slices = (energy.Count - 1) / stride + 1;
        var scores = new float[slices, TempoCandidates];
        var hitsPerSlice = new int[slices];
        var bands = new RhythmBandEvidence(energy, hopsPerSecond, cancellationToken);
        var periodicity = new float[slices, TempoCandidates];
        var phaseEvidence = new float[slices, TempoCandidates];

        // Periodic phase-coherence evidence every two seconds, measured in 9-second windows.
        // Windowing prevents a tempo change in the middle of a song from cancelling the score.
        var indices = onsets.Select(o => o.Index).ToArray();
        var strengths = onsets.Select(o => o.Strength).ToArray();
        var accents = onsets.Select(BeatWeight).ToArray();
        var firstHits = new int[slices];
        var lastHits = new int[slices];
        var weights = new double[slices];
        var strongWeights = new double[slices];
        var useAccents = new bool[slices];
        var correlations = new RhythmBandEvidence.Periodicity[slices];
        int firstHit = 0, lastHit = 0;
        for (int window = 0; window < slices; window++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int center = window * stride;
            while (firstHit < onsets.Count && indices[firstHit] < center - radius) firstHit++;
            if (lastHit < firstHit) lastHit = firstHit;
            while (lastHit < onsets.Count && indices[lastHit] <= center + radius) lastHit++;
            firstHits[window] = firstHit;
            lastHits[window] = lastHit;
            hitsPerSlice[window] = lastHit - firstHit;
            if (hitsPerSlice[window] < 8) continue;
            double totalWeight = 0, strongWeight = 0, strongSquares = 0;
            for (int k = firstHit; k < lastHit; k++)
            {
                totalWeight += strengths[k];
                strongWeight += accents[k];
                strongSquares += accents[k] * accents[k];
            }
            if (totalWeight <= 0) continue;
            weights[window] = totalWeight;
            strongWeights[window] = strongWeight;
            useAccents[window] = strongSquares > 0 && strongWeight * strongWeight / strongSquares >= 5;
            correlations[window] = bands.Measure(center - radius, center + radius + 1, cancellationToken);
        }

        // Compute each onset's complex contribution once per candidate, then
        // reuse it across all overlapping windows. Memory is O(onsets), not
        // O(onsets * candidates); no change to the measured evidence formula.
        var prefix = new RhythmPhasePrefix(onsets.Count);
        for (int candidate = 0; candidate < TempoCandidates; candidate++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double bpm = MinimumBpm + candidate * BpmResolution;
            prefix.Build(indices, strengths, accents, Math.Tau * bpm / (60 * hopsPerSecond), cancellationToken);
            for (int window = 0; window < slices; window++)
            {
                if (weights[window] <= 0) continue;
                var (real, imaginary, accentReal, accentImaginary) = prefix.Range(firstHits[window], lastHits[window]);
                double broad = Math.Clamp(Math.Sqrt(real * real + imaginary * imaginary) / weights[window], 0, 1);
                bool canUseAccents = useAccents[window];
                double prominent = canUseAccents
                    ? Math.Clamp(Math.Sqrt(accentReal * accentReal + accentImaginary * accentImaginary) / strongWeights[window], 0, 1)
                    : broad;
                double phase = canUseAccents ? .60 * prominent + .40 * broad : broad;
                double periodic = correlations[window].AtBpm(bpm);
                phaseEvidence[window, candidate] = (float)phase;
                periodicity[window, candidate] = (float)periodic;
                double combined = .65 * phase + .35 * Math.Min(1, periodic * 1.8);
                if (canUseAccents && prominent >= .8 && bpm <= 160)
                    combined = Math.Max(combined, .85 * prominent + .15 * broad);
                scores[window, candidate] = (float)combined;
            }
        }

        // Ranking, evidence and confidence remain separate: no octave can promote
        // a score that another octave has already modified.
        var tempo = RhythmTempoFamily.Resolve(scores, periodicity, phaseEvidence,
            MinimumBpm, BpmResolution, cancellationToken);
        var metricRoot = RhythmMetricHierarchy.Apply(scores, periodicity, phaseEvidence,
            tempo.Ranking, MinimumBpm, BpmResolution, cancellationToken);
        if (trace is not null)
        {
            trace.MetricRoot = metricRoot;
            trace.RawTempoScores = scores;
            trace.TempoScores = tempo.Ranking;
            trace.PeriodicityScores = periodicity;
            trace.PhaseScores = phaseEvidence;
            trace.ConfidenceScores = tempo.Confidence;
        }

        // Tempo Viterbi: favor continuity, but allow accelerando, ritardando and actual changes.
        // A weak or silent section inherits the neighboring estimate rather than generating noise.
        var backtrack = new short[slices, TempoCandidates];
        var previousScore = new double[TempoCandidates];
        var nextScore = new double[TempoCandidates];
        for (int candidate = 0; candidate < TempoCandidates; candidate++)
            previousScore[candidate] = tempo.Ranking[0, candidate];
        for (int window = 1; window < slices; window++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int globalBest = Array.IndexOf(previousScore, previousScore.Max());
            for (int candidate = 0; candidate < TempoCandidates; candidate++)
            {
                // Permit genuine abrupt tempo changes; a fixed transition cost still
                // requires multiple windows of evidence to leave a stable tempo.
                int best = globalBest;
                double bestValue = previousScore[globalBest] - .45;
                for (int earlier = Math.Max(0, candidate - 24);
                     earlier <= Math.Min(TempoCandidates - 1, candidate + 24); earlier++)
                {
                    double penalty = .022 * Math.Abs(candidate - earlier) * BpmResolution;
                    double value = previousScore[earlier] - penalty;
                    if (value <= bestValue) continue;
                    best = earlier;
                    bestValue = value;
                }
                nextScore[candidate] = bestValue + tempo.Ranking[window, candidate];
                backtrack[window, candidate] = (short)best;
            }
            (previousScore, nextScore) = (nextScore, previousScore);
        }

        int bestLast = Array.IndexOf(previousScore, previousScore.Max());
        var selected = new double[slices];
        var quality = new float[slices];
        var eligibleWindows = new bool[slices];
        for (int window = slices - 1; window >= 0; window--)
        {
            selected[window] = MinimumBpm + bestLast * BpmResolution;
            quality[window] = tempo.Confidence[window, bestLast];
            // Low-energy/syncopated music may have weaker attacks, but require
            // corroboration both within this window and across the track.
            bool corroborated = scores[window, bestLast] >= .15f &&
                periodicity[window, bestLast] >= .105f &&
                tempo.TrackPeriodicity[bestLast] >= .085f &&
                phaseEvidence[window, bestLast] >= .10f;
            // A high onset-coherence score alone can arise by chance on noise.
            // Strong phase can rescue changing or isolated tempos; weak phase
            // must be corroborated by independent band periodicity.
            bool rhythmicEvidence = periodicity[window, bestLast] >= .105f ||
                phaseEvidence[window, bestLast] >= .60f && scores[window, bestLast] >= .48f;
            eligibleWindows[window] = hitsPerSlice[window] >= 8 &&
                (quality[window] >= .28f && rhythmicEvidence || corroborated);
            if (window > 0) bestLast = backtrack[window, bestLast];
        }
        // Retain the result of EVERY analysis window, including rejected ones. This
        // explains failures in exported reports instead of only reporting "BPM = 0".
        var windows = new TrackTempoWindow[slices];
        for (int index = 0; index < slices; index++)
        {
            // These are real 9-second analysis windows, not the 2-second center spacing.
            long start = Math.Clamp(((long)index * stride - radius) * framesPerHop, 0, totalFrames);
            long end = Math.Clamp(((long)index * stride + radius) * framesPerHop, start, totalFrames);
            windows[index] = new(start, end, hitsPerSlice[index] >= 5 ? selected[index] : 0,
                quality[index], hitsPerSlice[index])
            {
                Accepted = eligibleWindows[index],
                SubdivisionFactor = eligibleWindows[index]
                    ? RhythmMetricHierarchy.SubdivisionAt(scores, periodicity, index, selected[index],
                        MinimumBpm, BpmResolution) : 1
            };
        }

        var sections = new List<TrackTempoSection>();
        var beats = new List<long>();
        var predicted = new List<TrackBeatEstimate>();
        int firstWindow = -1;
        for (int index = 0; index <= slices; index++)
        {
            bool eligible = index < slices && eligibleWindows[index];
            bool tempoChange = eligible && firstWindow >= 0 &&
                Math.Abs(selected[index] - selected[firstWindow]) > 7.5 && index - firstWindow >= 2;
            if ((!eligible || tempoChange || index == slices) && firstWindow >= 0)
            {
                // A single strong window may be a melody or a random repeating sound.
                if (index - firstWindow >= 2)
                    AnalyzeSectionWithRecovery(firstWindow, index, selected, quality, onsets, stride, radius,
                        hopsPerSecond, framesPerHop, totalFrames, sections, beats, predicted, bands, cancellationToken, trace);
                firstWindow = -1;
            }
            if (eligible && firstWindow < 0) firstWindow = index;
        }

        RecoverUncoveredSections(sections, beats, predicted, selected, quality, eligibleWindows,
            periodicity, onsets, bands, stride, radius, hopsPerSecond, framesPerHop, totalFrames,
            cancellationToken, trace);
        AlignSectionPhases(sections, beats, predicted, bands, hopsPerSecond, framesPerHop,
            cancellationToken, trace);
        RecoverCompatibleGaps(sections, beats, predicted, onsets, bands, hopsPerSecond,
            framesPerHop, cancellationToken, trace);
        ExtendSupportedTails(sections, beats, predicted, onsets, bands, hopsPerSecond,
            framesPerHop, totalFrames, cancellationToken);
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            double windowFrames = (double)stride * framesPerHop;
            int first = Math.Clamp((int)Math.Ceiling(section.StartFrame / windowFrames), 0, slices - 1);
            int end = Math.Clamp((int)Math.Floor((section.EndFrame - 1) / windowFrames), first, slices - 1);
            int[] counts = new int[5];
            for (int w = first; w <= end; w++)
            {
                if (!windows[w].Accepted || Math.Abs(windows[w].Bpm - section.Bpm) > section.Bpm * .04) continue;
                counts[windows[w].SubdivisionFactor]++;
            }
            int best = 1;
            for (int factor = 2; factor <= 4; factor++)
                if (counts[factor] > counts[best] && counts[factor] >= 2) best = factor;
            sections[i] = section with { SubdivisionFactor = best };
        }

        if (beats.Count == 0)
            return Empty(transients) with
            {
                Windows = windows,
                PredictedBeatFrames = predicted.Select(estimate => estimate.Frame).ToArray(),
                BeatEstimates = predicted.ToArray(),
                CandidateOnsets = onsets.Count,
                OnsetDiagnostics = onsetDebug,
                AnalysisStatus = "Нет подтверждённых локальных участков с устойчивыми долями"
            };

        // Segments may be separated by a breakdown. In that case no artificial beats
        // are inserted in the gap, and BPM belongs to the corresponding section.
        // Weight by reliable duration, not by the number of short recovery fragments.
        long covered = sections.Sum(section => section.EndFrame - section.StartFrame);
        long cumulative = 0;
        double representative = sections[0].Bpm;
        foreach (var section in sections.OrderBy(section => section.Bpm))
        {
            cumulative += section.EndFrame - section.StartFrame;
            representative = section.Bpm;
            if (cumulative >= covered / 2d) break;
        }
        float confidence = (float)(sections.Sum(section =>
            (section.EndFrame - section.StartFrame) * (double)section.Confidence) / covered);
        return new TrackBeatGrid(representative, beats[0], confidence,
            StoreTransients(onsets, framesPerHop, totalFrames, .65f))
        {
            BeatFrames = beats.ToArray(),
            PredictedBeatFrames = predicted.Select(estimate => estimate.Frame).ToArray(),
            BeatEstimates = predicted.ToArray(),
            Sections = sections.ToArray(),
            Windows = windows,
            CandidateOnsets = onsets.Count,
            OnsetDiagnostics = onsetDebug,
            AnalysisStatus = $"Найдено участков: {sections.Count}"
        };
    }

    // A single long-range phase lock can fail even when the tempo windows are
    // coherent (e.g. changing kick timbre, a phase reset, or a syncopated break).
}
