namespace Laveyana.Audio.Playback;

/// <summary>One fixed-tempo beat-grid estimate, with independent measured transient positions.</summary>
/// <remarks>BPM and grid origin are estimates, not verified downbeats or variable-tempo markers.</remarks>
public sealed record TrackBeatGrid(double Bpm, long FirstBeatFrame, float Confidence, long[] TransientFrames)
{
    public bool HasBeatGrid => Bpm >= 70 && Confidence >= .43f;
}

/// <summary>Derives a tempo candidate and onset times from the saved three-band waveform, without decoding twice.</summary>
public static class TrackRhythmAnalyzer
{
    private const double MinimumBpm = 70;
    private const double MaximumBpm = 190;
    private const int MinimumOnsets = 8;

    public static bool IsValid(TrackBeatGrid rhythm, long totalFrames) =>
        rhythm.Bpm >= 0 && rhythm.Bpm <= MaximumBpm && double.IsFinite(rhythm.Bpm) &&
        rhythm.Confidence >= 0 && rhythm.Confidence <= 1 && float.IsFinite(rhythm.Confidence) &&
        rhythm.FirstBeatFrame >= 0 && rhythm.FirstBeatFrame <= totalFrames &&
        rhythm.TransientFrames is { Length: <= 8192 } onsets &&
        onsets.All(frame => frame >= 0 && frame <= totalFrames);

    public static TrackBeatGrid Analyze(TrackWaveform waveform)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        var points = waveform.Points;
        int count = points.Count;
        if (count < 16 || waveform.TotalFrames <= 0 || waveform.FramesPerPoint <= 0)
            return new TrackBeatGrid(0, 0, 0, []);

        var novelty = new float[count];
        float previous = 0, earlier = 0, maximum = 0;
        for (int i = 0; i < count; i++)
        {
            // Positive multi-band energy changes approximate percussive spectral flux.
            var point = points[i];
            float energy = Math.Max(0, point.Low) * 1.6f + Math.Max(0, point.Mid) * .85f +
                Math.Max(0, point.High) * .5f + Math.Max(0, point.Peak) * .16f;
            float baseline = i == 0 ? energy : .75f * previous + .25f * earlier;
            novelty[i] = Math.Max(0, energy - baseline);
            maximum = Math.Max(maximum, novelty[i]);
            earlier = previous;
            previous = energy;
        }
        if (maximum < .018f) return new TrackBeatGrid(0, 0, 0, []);

        var onsets = new List<(int Index, float Strength)>();
        float threshold = Math.Max(.018f, maximum * .13f);
        for (int i = 1; i < count - 1; i++)
        {
            if (novelty[i] >= threshold && novelty[i] > novelty[i - 1] && novelty[i] >= novelty[i + 1])
            {
                if (onsets.Count > 0 && i - onsets[^1].Index < 2)
                {
                    if (novelty[i] > onsets[^1].Strength) onsets[^1] = (i, novelty[i]);
                }
                else onsets.Add((i, novelty[i]));
            }
        }
        long[] transients = onsets.Select(hit => Math.Min(waveform.TotalFrames - 1,
            (long)hit.Index * waveform.FramesPerPoint)).ToArray();
        if (onsets.Count < MinimumOnsets) return new TrackBeatGrid(0, 0, 0, transients);

        double rate = (double)PlaybackFormat.SampleRate / waveform.FramesPerPoint;
        double bestBpm = 0, bestScore = 0;
        // Tempo scoring tests repetition after 1, 2 and 4 beats; fractional point offsets
        // keep accuracy above the source waveform's coarse 25 Hz time resolution.
        for (double bpm = MinimumBpm; bpm <= MaximumBpm; bpm += .5)
        {
            double step = rate * 60 / bpm;
            double sum = 0, weight = 0;
            foreach (var hit in onsets)
            {
                foreach (int beats in new[] { 1, 2, 4 })
                {
                    int center = (int)Math.Round(hit.Index + beats * step);
                    if (center + 1 >= count) continue;
                    float match = Math.Max(novelty[center - 1], Math.Max(novelty[center], novelty[center + 1]));
                    float multiplier = beats == 1 ? 1f : beats == 2 ? .8f : .6f;
                    sum += multiplier * hit.Strength * Math.Min(1, match / maximum);
                    weight += multiplier * hit.Strength;
                }
            }
            if (weight <= 0) continue;
            double score = sum / weight;
            // The candidate must explain consecutive beats, not only distant subdivisions.
            if (score > bestScore)
            {
                bestScore = score;
                bestBpm = bpm;
            }
        }
        float confidence = (float)Math.Clamp(bestScore, 0, 1);
        if (confidence < .43f) return new TrackBeatGrid(0, 0, confidence, transients);

        double period = rate * 60 / bestBpm;
        double origin = 0, bestAlignment = -1;
        // Use strong onsets as phase candidates rather than assuming the first sample starts on a beat.
        var anchors = onsets.OrderByDescending(hit => hit.Strength).Take(64).ToArray();
        foreach (var candidate in anchors)
        {
            double phase = candidate.Index % period;
            double alignment = 0;
            foreach (var onset in onsets)
            {
                double distance = Math.Abs(onset.Index - phase) % period;
                distance = Math.Min(distance, period - distance);
                double fraction = distance / period;
                if (fraction < .16) alignment += onset.Strength * (1 - fraction / .16);
            }
            if (alignment <= bestAlignment) continue;
            bestAlignment = alignment;
            origin = phase;
        }
        long firstBeat = Math.Clamp((long)Math.Round(origin * waveform.FramesPerPoint), 0, waveform.TotalFrames);
        return new TrackBeatGrid(bestBpm, firstBeat, confidence, transients);
    }
}
