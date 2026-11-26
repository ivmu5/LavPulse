namespace Laveyana.Audio.Playback;

/// <summary>Band energy averaged over a small, fixed PCM window; used only while analyzing a track.</summary>
internal readonly record struct RhythmEnergy(float Low, float Mid, float High);

/// <summary>An independently evaluated rhythmic region; gaps are deliberately left without grid marks.</summary>
public sealed record TrackTempoSection(long StartFrame, long EndFrame, double Bpm, float Confidence,
    int OnsetCount, int MatchedBeats)
{
    /// <summary>Observed faster pulse rate, not a verified time signature.</summary>
    public int SubdivisionFactor { get; init; } = 1;
    public double SubdivisionBpm => Bpm * SubdivisionFactor;
}

/// <summary>One local analysis window, including rejected windows for diagnostic export.</summary>
public sealed record TrackTempoWindow(long StartFrame, long EndFrame, double Bpm, float Confidence, int OnsetCount)
{
    /// <summary>A rejected window still reports its strongest tempo hypothesis for diagnostics.</summary>
    public bool Accepted { get; init; }
    public int SubdivisionFactor { get; init; } = 1;
    public double SubdivisionBpm => Bpm * SubdivisionFactor;
}

/// <summary>A percussive candidate preserved for diagnosing false detections and missing beats.</summary>
public readonly record struct TrackOnsetDiagnostic(long Frame, float Strength, float Accent);

/// <summary>Local pulse evidence. Confidence is a heuristic score, not a calibrated probability.</summary>
public readonly record struct TrackBeatEstimate(long Frame, float Confidence, int CorrectionFrames,
    bool MatchedOnset, bool EnergyBreak, bool Confirmed);

/// <summary>Estimated tempo, measured attacks and a variable-tempo sequence of beat positions.</summary>
/// <remarks>Beats are estimated musical pulses, not verified bar downbeats or phrase boundaries.</remarks>
public sealed record TrackBeatGrid(double Bpm, long FirstBeatFrame, float Confidence, long[] TransientFrames)
{
    /// <summary>Individually tracked beat positions (48 kHz PCM frame coordinates).</summary>
    public long[] BeatFrames { get; init; } = [];
    /// <summary>Unverified pulse predictions; never use as confirmed visual or mixing beats.</summary>
    public long[] PredictedBeatFrames { get; init; } = [];
    /// <summary>Evidence for individual predictions, including rejected regions.</summary>
    public TrackBeatEstimate[] BeatEstimates { get; init; } = [];
    public TrackTempoSection[] Sections { get; init; } = [];
    public TrackTempoWindow[] Windows { get; init; } = [];
    public int CandidateOnsets { get; init; }
    public TrackOnsetDiagnostic[] OnsetDiagnostics { get; init; } = [];
    public string AnalysisStatus { get; init; } = "";

    // A track can contain trustworthy rhythmic sections even if its intro/breakdown is unmetered.
    public bool HasBeatGrid => BeatFrames.Length >= 4 &&
        (Sections.Length > 0 || (Bpm >= 40 && Confidence >= .40f));

    /// <summary>Estimates the local BPM from neighboring beats instead of the track-wide median.</summary>
    public double BpmAt(long position)
    {
        if (!HasBeatGrid) return Bpm;
        // Binary-search disjoint rhythmic regions, then estimate tempo from
        // nearby intervals within the same section. Never cross a breakdown.
        int lo = 0, hi = Sections.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Sections[mid].EndFrame <= position) lo = mid + 1;
            else hi = mid;
        }
        if (Sections.Length > 0)
        {
            if (lo == Sections.Length || position < Sections[lo].StartFrame) return 0;
            var section = Sections[lo];
            int sectionFirst = BeatIndex(section.StartFrame), end = BeatIndex(section.EndFrame);
            if (end - sectionFirst < 4) return section.Bpm;
            int center = Math.Clamp(BeatIndex(position), sectionFirst, end - 1);
            int from = Math.Clamp(center - 4, sectionFirst, Math.Max(sectionFirst, end - 9));
            int to = Math.Min(end - 1, from + 8);
            Span<long> intervals = stackalloc long[8];
            int count = 0;
            for (int n = from; n < to; n++)
            {
                long delta = BeatFrames[n + 1] - BeatFrames[n];
                if (delta > 0) intervals[count++] = delta;
            }
            if (count < 3) return section.Bpm;
            var valid = intervals[..count];
            valid.Sort();
            double median = (valid[(count - 1) / 2] + (double)valid[count / 2]) / 2;
            double total = 0;
            int inliers = 0;
            foreach (long interval in valid)
                if (Math.Abs(interval - median) <= .18 * median)
                { total += interval; inliers++; }
            if (inliers < 3 || total <= 0) return section.Bpm;
            double bpm = 60d * PlaybackFormat.SampleRate * inliers / total;
            return bpm >= 40 && bpm <= 240 && Math.Abs(bpm - section.Bpm) <= .18 * section.Bpm
                ? bpm : section.Bpm;
        }
        int index = Array.BinarySearch(BeatFrames, position);
        if (index < 0) index = ~index;
        int span = Math.Min(4, BeatFrames.Length - 1);
        int first = Math.Clamp(index - span / 2, 0, BeatFrames.Length - 1 - span);
        long distance = BeatFrames[first + span] - BeatFrames[first];
        return distance > 0 ? Math.Clamp(span * 60d * PlaybackFormat.SampleRate / distance, 40, 240) : Bpm;
    }

    private int BeatIndex(long frame)
    {
        int index = Array.BinarySearch(BeatFrames, frame);
        return index < 0 ? ~index : index;
    }
}

/// <summary>
/// Detects 10 ms percussive attacks, estimates tempo in short overlapping windows, smooths
/// tempo changes and locks each beat to nearby measured onsets. All work is off the UI thread.
/// </summary>
public static class TrackRhythmAnalyzer
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
        sections.All(section => section.StartFrame >= 0 && section.StartFrame < section.EndFrame &&
            section.EndFrame <= totalFrames && section.Bpm >= MinimumBpm &&
            section.Bpm <= MaximumBpm && double.IsFinite(section.Bpm) &&
            float.IsFinite(section.Confidence) && section.Confidence >= 0 && section.Confidence <= 1 &&
            section.OnsetCount >= 0 && section.MatchedBeats >= 0 &&
            section.SubdivisionFactor is >= 1 and <= 4) &&
        windows.All(window => window.StartFrame >= 0 && window.StartFrame <= window.EndFrame &&
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
        long startFrame = firstWindow == 0 ? 0 :
            Math.Max(0L, ((long)firstWindow * stride - stride / 2) * hopFrames);
        long endFrame = endWindow == tempos.Length ? totalFrames :
            Math.Min(totalFrames, ((long)(endWindow - 1) * stride + stride / 2) * hopFrames);
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
                var correlation = bands.Measure((int)(section.StartFrame / hopFrames),
                    (int)Math.Ceiling(section.EndFrame / (double)hopFrames), token);
                double normal = correlation.AtBpm(section.Bpm);
                double doubled = correlation.AtBpm(section.Bpm * 2);
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
            double q = bands.Measure((int)(anchor / hopFrames),
                (int)Math.Ceiling(tail[^1].Frame / (double)hopFrames), token).AtBpm(section.Bpm);
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
            double correlation = bands.Measure(from, to, token).AtBpm(bpm);
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

    private static double LocalPhaseCorrection(IReadOnlyList<Onset> onsets, double position,
        double spacing, double hopsPerSecond, bool useStrong, int missed, double start, double end)
    {
        double radius = 2.1 * hopsPerSecond;
        double first = Math.Max(start, position - radius), last = Math.Min(end, position + radius);
        double real = 0, imaginary = 0, total = 0, squares = 0;
        for (int i = LowerBound(onsets, first); i < onsets.Count && onsets[i].Index <= last; i++)
        {
            var onset = onsets[i];
            double weight = useStrong ? Math.Max(BeatWeight(onset) * 2, onset.Strength * .20) : onset.Strength;
            double distance = Math.Abs(onset.Index - position) / radius;
            weight *= 1 - .6 * Math.Min(1, distance);
            (double sine, double cosine) = Math.SinCos((onset.Index - position) * Math.Tau / spacing);
            real += weight * cosine;
            imaginary += weight * sine;
            total += weight;
            squares += weight * weight;
        }
        if (squares <= 0 || total * total / squares < 4) return 0;
        double coherence = Math.Sqrt(real * real + imaginary * imaginary) / total;
        if (coherence < (useStrong ? .38 : .30)) return 0;
        double delta = Math.Atan2(imaginary, real) * spacing / Math.Tau;
        return Math.Clamp(delta * (missed >= 3 ? .85 : .45), -spacing * .20, spacing * .20);
    }

    private static int LowerBound(IReadOnlyList<Onset> onsets, double index)
    {
        int low = 0, high = onsets.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (onsets[mid].Index < index) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static double TempoAt(double hop, double[] tempi, int stride, int firstWindow, int endWindow)
    {
        double index = Math.Clamp(hop / stride, firstWindow, endWindow - 1);
        int first = (int)index;
        int second = Math.Min(first + 1, endWindow - 1);
        return tempi[first] + (tempi[second] - tempi[first]) * Math.Clamp(index - first, 0, 1);
    }

    private static List<Onset> DetectOnsets(IReadOnlyList<RhythmEnergy> energy, double hopsPerSecond,
        CancellationToken token)
    {
        int count = energy.Count;
        var novelty = new float[count];
        var bassChange = new float[count];
        var midChange = new float[count];
        var prefix = new double[count + 1];
        for (int i = 1; i < count; i++)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            RhythmEnergy now = energy[i], before = energy[i - 1];
            bassChange[i] = Math.Max(0, now.Low - before.Low);
            midChange[i] = Math.Max(0, now.Mid - before.Mid);
            // High-only clicks, hats and harmonics are weak beat evidence.
            novelty[i] = bassChange[i] * 1.9f + midChange[i] * .85f +
                Math.Max(0, now.High - before.High) * .25f;
            prefix[i + 1] = prefix[i] + novelty[i];
        }

        int radius = Math.Max(2, (int)Math.Round(hopsPerSecond));
        int minDistance = Math.Max(2, (int)Math.Round(hopsPerSecond * .08));
        var result = new List<Onset>();
        for (int i = 1; i < count - 1; i++)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            int first = Math.Max(0, i - radius), last = Math.Min(count, i + radius + 1);
            float average = (float)((prefix[last] - prefix[first]) / (last - first));
            float rise = novelty[i];
            // Relative thresholds keep quietly mastered tracks measurable. Compare each
            // candidate against both nearby attacks and its local band energy.
            float energyAtPeak = energy[i].Low * 1.9f + energy[i].Mid * .85f + energy[i].High * .25f;
            if (rise < Math.Max(.000001f, average * 1.8f) ||
                rise < energyAtPeak * .04f ||
                rise <= novelty[i - 1] || rise < novelty[i + 1]) continue;

            float bassShare = Math.Clamp(bassChange[i] * 1.9f / rise, 0, 1);
            float midShare = Math.Clamp(midChange[i] * .85f / rise, 0, 1);
            float prominence = rise / (rise + Math.Max(.000001f, average * 2));
            float strength = prominence * (.16f + 1.7f * bassShare + .75f * midShare);
            float accent = prominence * (1.3f * bassShare + .9f * midShare);
            if (result.Count > 0 && i - result[^1].Index < minDistance)
            {
                if (strength > result[^1].Strength)
                    result[^1] = new Onset(i, strength, accent);
            }
            else result.Add(new Onset(i, strength, accent));
        }
        return result;
    }

    /// <summary>Store only prominent low/mid attacks for the small visual markers.</summary>
    private static long[] StoreTransients(List<Onset> onsets, int hopFrames, long totalFrames, float minimumAccent)
    {
        int minimumDistance = Math.Max(2, (int)Math.Round(.16 * PlaybackFormat.SampleRate / hopFrames));
        var accents = new List<Onset>();
        foreach (Onset onset in onsets)
        {
            if (onset.Accent < minimumAccent) continue;
            if (accents.Count > 0 && onset.Index - accents[^1].Index < minimumDistance)
            {
                if (onset.Accent > accents[^1].Accent) accents[^1] = onset;
            }
            else accents.Add(onset);
        }
        int stride = Math.Max(1, (int)Math.Ceiling(accents.Count / (double)MaximumStoredTransients));
        return accents.Where((_, index) => index % stride == 0)
            .Select(hit => Math.Min(totalFrames - 1, (long)hit.Index * hopFrames)).ToArray();
    }

    private static TrackOnsetDiagnostic[] SampleCandidates(IReadOnlyList<Onset> onsets,
        int framesPerHop, long totalFrames)
    {
        int step = Math.Max(1, (int)Math.Ceiling(onsets.Count / (double)MaximumStoredTransients));
        var result = new List<TrackOnsetDiagnostic>();
        for (int i = 0; i < onsets.Count; i += step)
        {
            var hit = onsets[i];
            result.Add(new(Math.Min(totalFrames - 1, (long)hit.Index * framesPerHop),
                hit.Strength, hit.Accent));
        }
        return result.ToArray();
    }

    private static TrackBeatGrid Empty(long[]? transients = null) => new(0, 0, 0, transients ?? []);
}
