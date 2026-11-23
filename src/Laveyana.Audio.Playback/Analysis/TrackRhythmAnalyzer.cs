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

        double pointsPerSecond = (double)PlaybackFormat.SampleRate / waveform.FramesPerPoint;
        double bestBpm = 0, bestScore = 0, bestAngle = 0;
        // Circular onset-phase coherence scores all strong attacks across the entire track.
        // Unlike short-lag peak matching, a wrong tempo accumulates phase drift and loses score.
        // The lowest equally strong candidate wins the occasional half/double-time ambiguity.
        for (double bpm = MinimumBpm; bpm <= MaximumBpm; bpm += .25)
        {
            double radiansPerPoint = Math.Tau * bpm / (60 * pointsPerSecond);
            double real = 0, imaginary = 0, weight = 0;
            foreach (var onset in onsets)
            {
                (double sine, double cosine) = Math.SinCos(onset.Index * radiansPerPoint);
                real += onset.Strength * cosine;
                imaginary += onset.Strength * sine;
                weight += onset.Strength;
            }
            if (weight <= 0) continue;
            double score = Math.Sqrt(real * real + imaginary * imaginary) / weight;
            if (score > bestScore + .001)
            {
                bestScore = score;
                bestBpm = bpm;
                bestAngle = Math.Atan2(imaginary, real);
            }
        }
        float confidence = (float)Math.Clamp(bestScore, 0, 1);
        if (confidence < .43f) return new TrackBeatGrid(0, 0, confidence, transients);

        double period = pointsPerSecond * 60 / bestBpm;
        double phase = (bestAngle + Math.Tau) % Math.Tau;
        double origin = phase / Math.Tau * period;
        long firstBeat = Math.Clamp((long)Math.Round(origin * waveform.FramesPerPoint), 0, waveform.TotalFrames);
        return new TrackBeatGrid(bestBpm, firstBeat, confidence, transients);
    }
}
