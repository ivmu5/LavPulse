namespace Laveyana.Audio.Playback;

/// <summary>Band energy averaged over a small, fixed PCM window; used only while analyzing a track.</summary>
internal readonly record struct RhythmEnergy(float Low, float Mid, float High);

/// <summary>Estimated tempo, measured attacks and a variable-tempo sequence of beat positions.</summary>
/// <remarks>Beats are estimated musical pulses, not verified bar downbeats or phrase boundaries.</remarks>
public sealed record TrackBeatGrid(double Bpm, long FirstBeatFrame, float Confidence, long[] TransientFrames)
{
    /// <summary>Individually tracked beat positions (48 kHz PCM frame coordinates).</summary>
    public long[] BeatFrames { get; init; } = [];

    public bool HasBeatGrid => Bpm >= 70 && Confidence >= .46f && BeatFrames.Length >= 4;

    /// <summary>Estimates the local BPM from neighboring beats instead of the track-wide median.</summary>
    public double BpmAt(long position)
    {
        if (!HasBeatGrid) return Bpm;
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

    private readonly record struct Onset(int Index, float Strength);

    public static bool IsValid(TrackBeatGrid rhythm, long totalFrames) =>
        rhythm.Bpm >= 0 && rhythm.Bpm <= MaximumBpm && double.IsFinite(rhythm.Bpm) &&
        rhythm.Confidence >= 0 && rhythm.Confidence <= 1 && float.IsFinite(rhythm.Confidence) &&
        rhythm.FirstBeatFrame >= 0 && rhythm.FirstBeatFrame <= totalFrames &&
        rhythm.TransientFrames is { Length: <= MaximumStoredTransients } onsets &&
        rhythm.BeatFrames is { Length: <= MaximumBeatFrames } beats &&
        (beats.Length == 0 || (beats.Length >= 4 && beats[0] == rhythm.FirstBeatFrame)) &&
        IsAscending(onsets, totalFrames) && IsAscending(beats, totalFrames);

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
        if (totalFrames <= 0 || energy.Count < 16) return Empty();
        double hopsPerSecond = PlaybackFormat.SampleRate / (double)framesPerHop;
        var onsets = DetectOnsets(energy, hopsPerSecond, cancellationToken);
        long[] transients = StoreTransients(onsets, framesPerHop, totalFrames);
        if (onsets.Count < 8) return Empty(transients);

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
        var evidence = quality.Where((q, i) => hitsPerSlice[i] >= 8 && q >= .4f).ToArray();
        if (evidence.Length < 2) return Empty(transients);
        float coherence = evidence.Average();
        if (coherence < .52f) return Empty(transients);

        // Anchor the first beat from the phase of the earliest confident region.
        int anchor = Array.FindIndex(quality, q => q >= .52f);
        if (anchor < 0) return Empty(transients);
        double anchorBpm = selected[anchor];
        double angularSpeed = Math.Tau * anchorBpm / (60 * hopsPerSecond);
        int anchorCenter = anchor * stride;
        double re = 0, im = 0;
        foreach (Onset onset in onsets)
        {
            if (Math.Abs(onset.Index - anchorCenter) > radius) continue;
            (double sine, double cosine) = Math.SinCos(onset.Index * angularSpeed);
            re += onset.Strength * cosine;
            im += onset.Strength * sine;
        }
        double angle = (Math.Atan2(im, re) + Math.Tau) % Math.Tau;
        double period = 60 * hopsPerSecond / anchorBpm;
        double position = angle / Math.Tau * period;
        var beats = new List<long>();
        int onsetCursor = 0, missed = 0, matched = 0;
        while (position * framesPerHop < totalFrames && beats.Count < MaximumBeatFrames)
        {
            if ((beats.Count & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            double currentBpm = TempoAt(position, selected, stride);
            double spacing = 60 * hopsPerSecond / currentBpm;
            double searchRadius = spacing * (missed >= 3 ? .32 : .16);
            while (onsetCursor < onsets.Count && onsets[onsetCursor].Index < position - searchRadius)
                onsetCursor++;

            double bestOnset = position, bestMatch = 0;
            for (int i = onsetCursor; i < onsets.Count && onsets[i].Index <= position + searchRadius; i++)
            {
                double distance = Math.Abs(onsets[i].Index - position);
                double match = onsets[i].Strength * (1 - distance / searchRadius);
                if (match <= bestMatch) continue;
                bestMatch = match;
                bestOnset = onsets[i].Index;
            }
            double correction = 0;
            if (bestMatch >= .12)
            {
                correction = Math.Clamp((bestOnset - position) * (missed >= 3 ? .65 : .40),
                    -spacing * .20, spacing * .20);
                matched++;
                missed = 0;
            }
            else missed++;

            long frame = Math.Clamp((long)Math.Round((position + correction) * framesPerHop), 0, totalFrames - 1);
            if (beats.Count == 0 || frame > beats[^1]) beats.Add(frame);
            position += spacing + correction * .6;
        }

        if (beats.Count < 4) return Empty(transients);
        double[] tempi = selected.Where((_, i) => hitsPerSlice[i] >= 8 && quality[i] >= .4f)
            .OrderBy(b => b).ToArray();
        double representativeBpm = tempi[tempi.Length / 2];
        float confidence = (float)Math.Clamp(coherence * .82 + .18 * matched / Math.Max(1, beats.Count), 0, 1);
        return new TrackBeatGrid(representativeBpm, beats[0], confidence, transients)
        {
            BeatFrames = beats.ToArray()
        };
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
        var prefix = new double[count + 1];
        for (int i = 1; i < count; i++)
        {
            if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
            RhythmEnergy now = energy[i], before = energy[i - 1];
            bassChange[i] = Math.Max(0, now.Low - before.Low);
            novelty[i] = bassChange[i] * 1.9f + Math.Max(0, now.Mid - before.Mid) * .85f +
                Math.Max(0, now.High - before.High) * .45f;
            prefix[i + 1] = prefix[i] + novelty[i];
        }
        int radius = Math.Max(2, (int)Math.Round(hopsPerSecond));
        int minDistance = Math.Max(2, (int)Math.Round(hopsPerSecond * .06));
        var result = new List<Onset>();
        for (int i = 1; i < count - 1; i++)
        {
            int from = Math.Max(0, i - radius), to = Math.Min(count, i + radius + 1);
            float average = (float)((prefix[to] - prefix[from]) / (to - from));
            if (novelty[i] < Math.Max(.012f, average * 1.55f) ||
                novelty[i] <= novelty[i - 1] || novelty[i] < novelty[i + 1]) continue;
            float bassShare = novelty[i] > 0 ? bassChange[i] * 1.9f / novelty[i] : 0;
            float strength = novelty[i] / (novelty[i] + Math.Max(.04f, average * 2)) *
                (.5f + .8f * Math.Clamp(bassShare, 0, 1));
            if (result.Count > 0 && i - result[^1].Index < minDistance)
            {
                if (strength > result[^1].Strength) result[^1] = new Onset(i, strength);
            }
            else result.Add(new Onset(i, strength));
        }
        return result;
    }

    private static long[] StoreTransients(List<Onset> onsets, int hopFrames, long totalFrames)
    {
        int stride = Math.Max(1, (int)Math.Ceiling(onsets.Count / (double)MaximumStoredTransients));
        return onsets.Where((_, index) => index % stride == 0)
            .Select(hit => Math.Min(totalFrames - 1, (long)hit.Index * hopFrames)).ToArray();
    }

    private static TrackBeatGrid Empty(long[]? transients = null) => new(0, 0, 0, transients ?? []);
}
