namespace Laveyana.Audio.Playback;

/// <summary>Band energy averaged over a small, fixed PCM window; used only while analyzing a track.</summary>
internal readonly record struct RhythmEnergy(float Low, float Mid, float High);

/// <summary>An independently evaluated rhythmic region; gaps are deliberately left without grid marks.</summary>
public sealed record TrackTempoSection(long StartFrame, long EndFrame, double Bpm, float Confidence,
    int OnsetCount, int MatchedBeats);

/// <summary>One local analysis window, including rejected windows for diagnostic export.</summary>
public sealed record TrackTempoWindow(long StartFrame, long EndFrame, double Bpm, float Confidence, int OnsetCount);

/// <summary>A percussive candidate preserved for diagnosing false detections and missing beats.</summary>
public readonly record struct TrackOnsetDiagnostic(long Frame, float Strength, float Accent);

/// <summary>Estimated tempo, measured attacks and a variable-tempo sequence of beat positions.</summary>
/// <remarks>Beats are estimated musical pulses, not verified bar downbeats or phrase boundaries.</remarks>
public sealed record TrackBeatGrid(double Bpm, long FirstBeatFrame, float Confidence, long[] TransientFrames)
{
    /// <summary>Individually tracked beat positions (48 kHz PCM frame coordinates).</summary>
    public long[] BeatFrames { get; init; } = [];
    public TrackTempoSection[] Sections { get; init; } = [];
    public TrackTempoWindow[] Windows { get; init; } = [];
    public int CandidateOnsets { get; init; }
    public TrackOnsetDiagnostic[] OnsetDiagnostics { get; init; } = [];
    public string AnalysisStatus { get; init; } = "";

    // A track can contain trustworthy rhythmic sections even if its intro/breakdown is unmetered.
    public bool HasBeatGrid => BeatFrames.Length >= 4 &&
        (Sections.Length > 0 || (Bpm >= 70 && Confidence >= .40f));

    /// <summary>Estimates the local BPM from neighboring beats instead of the track-wide median.</summary>
    public double BpmAt(long position)
    {
        if (!HasBeatGrid) return Bpm;
        foreach (var section in Sections)
        {
            if (position >= section.StartFrame && position < section.EndFrame)
                return section.Bpm;
        }
        // Gaps between reliable rhythmic regions have no trustworthy local BPM.
        if (Sections.Length > 0) return 0;
        int index = Array.BinarySearch(BeatFrames, position);
        if (index < 0) index = ~index;
        int span = Math.Min(4, BeatFrames.Length - 1);
        int first = Math.Clamp(index - span / 2, 0, BeatFrames.Length - 1 - span);
        long distance = BeatFrames[first + span] - BeatFrames[first];
        return distance > 0 ? Math.Clamp(span * 60d * PlaybackFormat.SampleRate / distance, 40, 300) : Bpm;
    }
}

/// <summary>
/// Detects 10 ms percussive attacks, estimates tempo in short overlapping windows, smooths
/// tempo changes and locks each beat to nearby measured onsets. All work is off the UI thread.
/// </summary>
public static class TrackRhythmAnalyzer
{
    private const double MinimumBpm = 70;
    private const double MaximumBpm = 190;
    private const double BpmResolution = .5;
    private const int TempoCandidates = (int)((MaximumBpm - MinimumBpm) / BpmResolution) + 1;
    private const int MaximumStoredTransients = 8192;
    private const int MaximumBeatFrames = 32768;

    // Tempo estimation needs permissive onset candidates, but the visual hit markers
    // should represent only prominent low/mid attacks.
    private readonly record struct Onset(int Index, float Strength, float Accent);

    public static bool IsValid(TrackBeatGrid rhythm, long totalFrames) =>
        rhythm.Bpm >= 0 && rhythm.Bpm <= MaximumBpm && double.IsFinite(rhythm.Bpm) &&
        rhythm.Confidence >= 0 && rhythm.Confidence <= 1 && float.IsFinite(rhythm.Confidence) &&
        rhythm.FirstBeatFrame >= 0 && rhythm.FirstBeatFrame <= totalFrames &&
        rhythm.TransientFrames is { Length: <= MaximumStoredTransients } onsets &&
        rhythm.BeatFrames is { Length: <= MaximumBeatFrames } beats &&
        (beats.Length == 0 || (beats.Length >= 4 && beats[0] == rhythm.FirstBeatFrame)) &&
        IsAscending(onsets, totalFrames) && IsAscending(beats, totalFrames) &&
        rhythm.Sections is { Length: <= 4096 } sections &&
        rhythm.Windows is { Length: <= 8192 } windows &&
        sections.All(section => section.StartFrame >= 0 && section.StartFrame < section.EndFrame &&
            section.EndFrame <= totalFrames && section.Bpm >= MinimumBpm &&
            section.Bpm <= MaximumBpm && double.IsFinite(section.Bpm) &&
            float.IsFinite(section.Confidence) && section.Confidence >= 0 && section.Confidence <= 1 &&
            section.OnsetCount >= 0 && section.MatchedBeats >= 0) &&
        windows.All(window => window.StartFrame >= 0 && window.StartFrame <= window.EndFrame &&
            window.EndFrame <= totalFrames && window.Bpm >= 0 && window.Bpm <= MaximumBpm &&
            double.IsFinite(window.Bpm) && float.IsFinite(window.Confidence) &&
            window.Confidence >= 0 && window.Confidence <= 1 && window.OnsetCount >= 0) &&
        rhythm.CandidateOnsets >= 0 &&
        rhythm.OnsetDiagnostics is { Length: <= MaximumStoredTransients } diagnostics &&
        diagnostics.All(hit => hit.Frame >= 0 && hit.Frame <= totalFrames &&
            float.IsFinite(hit.Strength) && float.IsFinite(hit.Accent) &&
            hit.Strength >= 0 && hit.Accent >= 0);

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
        long totalFrames, CancellationToken cancellationToken = default)
    {
        if (framesPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerHop));
        if (totalFrames <= 0 || energy.Count < 16)
            return Empty() with { AnalysisStatus = "Недостаточно аудиоданных для анализа" };
        double hopsPerSecond = PlaybackFormat.SampleRate / (double)framesPerHop;
        var onsets = DetectOnsets(energy, hopsPerSecond, cancellationToken);
        // Without reliable rhythm, show only exceptionally strong isolated accents.
        // Relax the visual threshold only after the grid is independently validated.
        long[] transients = StoreTransients(onsets, framesPerHop, totalFrames, .85f);
        TrackOnsetDiagnostic[] onsetDebug = SampleCandidates(onsets, framesPerHop, totalFrames);
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

        // Periodic phase-coherence evidence every two seconds, measured in 9-second windows.
        // Windowing prevents a tempo change in the middle of a song from cancelling the score.
        int firstHit = 0, lastHit = 0;
        for (int window = 0; window < slices; window++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int center = window * stride;
            while (firstHit < onsets.Count && onsets[firstHit].Index < center - radius) firstHit++;
            if (lastHit < firstHit) lastHit = firstHit;
            while (lastHit < onsets.Count && onsets[lastHit].Index <= center + radius) lastHit++;
            int hitCount = lastHit - firstHit;
            hitsPerSlice[window] = hitCount;
            if (hitCount < 8) continue;
            double totalWeight = 0;
            for (int i = firstHit; i < lastHit; i++) totalWeight += onsets[i].Strength;
            if (totalWeight <= 0) continue;
            for (int candidate = 0; candidate < TempoCandidates; candidate++)
            {
                double bpm = MinimumBpm + candidate * BpmResolution;
                double anglePerHop = Math.Tau * bpm / (60 * hopsPerSecond);
                double real = 0, imaginary = 0;
                for (int i = firstHit; i < lastHit; i++)
                {
                    Onset onset = onsets[i];
                    (double sine, double cosine) = Math.SinCos(onset.Index * anglePerHop);
                    real += onset.Strength * cosine;
                    imaginary += onset.Strength * sine;
                }
                scores[window, candidate] = (float)(Math.Sqrt(real * real + imaginary * imaginary) / totalWeight);
            }
        }

        // Tempo Viterbi: favor continuity, but allow accelerando, ritardando and actual changes.
        // A weak or silent section inherits the neighboring estimate rather than generating noise.
        var backtrack = new short[slices, TempoCandidates];
        var previousScore = new double[TempoCandidates];
        var nextScore = new double[TempoCandidates];
        for (int candidate = 0; candidate < TempoCandidates; candidate++)
            previousScore[candidate] = scores[0, candidate];
        for (int window = 1; window < slices; window++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int candidate = 0; candidate < TempoCandidates; candidate++)
            {
                int best = candidate;
                double bestValue = double.NegativeInfinity;
                for (int earlier = Math.Max(0, candidate - 24);
                     earlier <= Math.Min(TempoCandidates - 1, candidate + 24); earlier++)
                {
                    double penalty = .022 * Math.Abs(candidate - earlier) * BpmResolution;
                    double value = previousScore[earlier] - penalty;
                    if (value <= bestValue) continue;
                    best = earlier;
                    bestValue = value;
                }
                nextScore[candidate] = bestValue + scores[window, candidate];
                backtrack[window, candidate] = (short)best;
            }
            (previousScore, nextScore) = (nextScore, previousScore);
        }

        int bestLast = Array.IndexOf(previousScore, previousScore.Max());
        var selected = new double[slices];
        var quality = new float[slices];
        for (int window = slices - 1; window >= 0; window--)
        {
            selected[window] = MinimumBpm + bestLast * BpmResolution;
            quality[window] = scores[window, bestLast];
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
                quality[index], hitsPerSlice[index]);
        }

        var sections = new List<TrackTempoSection>();
        var beats = new List<long>();
        int firstWindow = -1;
        for (int index = 0; index <= slices; index++)
        {
            bool eligible = index < slices && hitsPerSlice[index] >= 5 && quality[index] >= .28f;
            bool tempoChange = eligible && firstWindow >= 0 &&
                Math.Abs(selected[index] - selected[firstWindow]) > 7.5 && index - firstWindow >= 2;
            if ((!eligible || tempoChange || index == slices) && firstWindow >= 0)
            {
                // A single strong window may be a melody or a random repeating sound.
                if (index - firstWindow >= 2)
                    AnalyzeSection(firstWindow, index, selected, quality, onsets, stride, radius,
                        hopsPerSecond, framesPerHop, totalFrames, sections, beats, cancellationToken);
                firstWindow = -1;
            }
            if (eligible && firstWindow < 0) firstWindow = index;
        }

        if (beats.Count == 0)
            return Empty(transients) with
            {
                Windows = windows,
                CandidateOnsets = onsets.Count,
                OnsetDiagnostics = onsetDebug,
                AnalysisStatus = "Нет подтверждённых локальных участков с устойчивыми долями"
            };

        // Segments may be separated by a breakdown. In that case no artificial beats
        // are inserted in the gap, and BPM belongs to the corresponding section.
        var tempos = sections.Select(section => section.Bpm).OrderBy(value => value).ToArray();
        double representative = tempos[tempos.Length / 2];
        float confidence = sections.Average(section => section.Confidence);
        return new TrackBeatGrid(representative, beats[0], confidence,
            StoreTransients(onsets, framesPerHop, totalFrames, .65f))
        {
            BeatFrames = beats.ToArray(),
            Sections = sections.ToArray(),
            Windows = windows,
            CandidateOnsets = onsets.Count,
            OnsetDiagnostics = onsetDebug,
            AnalysisStatus = $"Найдено участков: {sections.Count}"
        };
    }

    private static void AnalyzeSection(int firstWindow, int endWindow, double[] tempos, float[] quality,
        IReadOnlyList<Onset> onsets, int stride, int radius, double hopsPerSecond,
        int hopFrames, long totalFrames, List<TrackTempoSection> sections, List<long> beats,
        CancellationToken token)
    {
        double meanConfidence = 0;
        var localTempos = new List<double>();
        for (int i = firstWindow; i < endWindow; i++)
        {
            meanConfidence += quality[i];
            localTempos.Add(tempos[i]);
        }
        meanConfidence /= endWindow - firstWindow;
        if (meanConfidence < .33) return;
        localTempos.Sort();
        double bpm = localTempos[localTempos.Count / 2];
        // Anchor phase close to the FIRST valid window. Starting from the middle of a long
        // accelerando with the median tempo introduces a cumulative phase error at its start.
        double anchorBpm = tempos[firstWindow];
        double period = hopsPerSecond * 60 / anchorBpm;
        double phaseSpeed = Math.Tau / period;
        int center = firstWindow * stride;
        double real = 0, imaginary = 0;
        int onsetsInSection = 0;
        foreach (Onset onset in onsets)
        {
            if (Math.Abs(onset.Index - center) <= radius)
            {
                (double sine, double cosine) = Math.SinCos(onset.Index * phaseSpeed);
                real += onset.Strength * cosine;
                imaginary += onset.Strength * sine;
            }
        }
        double phase = (Math.Atan2(imaginary, real) + Math.Tau) % Math.Tau;
        double phaseInHops = phase / Math.Tau * period;
        long startFrame = firstWindow == 0 ? 0 :
            Math.Max(0L, ((long)firstWindow * stride - stride / 2) * hopFrames);
        long endFrame = endWindow == tempos.Length ? totalFrames :
            Math.Min(totalFrames, ((long)(endWindow - 1) * stride + stride / 2) * hopFrames);
        if (startFrame >= endFrame) return;

        double fromHop = startFrame / (double)hopFrames;
        double position = phaseInHops + Math.Ceiling((fromHop - phaseInHops) / period) * period;
        int onsetCursor = 0;
        while (onsetCursor < onsets.Count && onsets[onsetCursor].Index < fromHop - period) onsetCursor++;

        var localBeats = new List<long>();
        int matched = 0, missed = 0;
        while (position * hopFrames < endFrame && localBeats.Count < MaximumBeatFrames)
        {
            if ((localBeats.Count & 1023) == 0) token.ThrowIfCancellationRequested();
            double localBpm = TempoAt(position, tempos, stride);
            double spacing = 60 * hopsPerSecond / localBpm;
            double searchRadius = spacing * (missed >= 3 ? .28 : .16);
            while (onsetCursor < onsets.Count && onsets[onsetCursor].Index < position - searchRadius)
                onsetCursor++;

            double strongest = 0, nearest = position;
            for (int i = onsetCursor; i < onsets.Count && onsets[i].Index <= position + searchRadius; i++)
            {
                double distance = Math.Abs(onsets[i].Index - position);
                double evidence = onsets[i].Strength * (1 - distance / searchRadius);
                if (evidence <= strongest) continue;
                strongest = evidence;
                nearest = onsets[i].Index;
            }
            double correction = 0;
            if (strongest >= .12)
            {
                correction = Math.Clamp((nearest - position) * (missed >= 3 ? .65 : .4),
                    -spacing * .2, spacing * .2);
                matched++;
                missed = 0;
            }
            else missed++;

            long frame = Math.Clamp((long)Math.Round((position + correction) * hopFrames), startFrame, endFrame - 1);
            if ((localBeats.Count == 0 || frame > localBeats[^1]) &&
                (beats.Count == 0 || frame > beats[^1]))
                localBeats.Add(frame);
            position += spacing + correction * .6;
        }
        if (localBeats.Count < 4 || matched < Math.Max(3, localBeats.Count / 6)) return;

        foreach (var onset in onsets)
            if (onset.Index * (long)hopFrames >= startFrame && onset.Index * (long)hopFrames < endFrame)
                onsetsInSection++;
        float confidence = (float)Math.Clamp(.78 * meanConfidence +
            .22 * matched / (double)localBeats.Count, 0, 1);
        sections.Add(new(startFrame, endFrame, bpm, confidence, onsetsInSection, matched));
        beats.AddRange(localBeats);
    }

    private static double TempoAt(double hop, double[] tempi, int stride)
    {
        double index = hop / stride;
        int first = Math.Clamp((int)index, 0, tempi.Length - 1);
        int second = Math.Min(first + 1, tempi.Length - 1);
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
