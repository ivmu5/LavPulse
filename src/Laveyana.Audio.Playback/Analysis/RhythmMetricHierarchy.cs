namespace Laveyana.Audio.Playback;

/// <summary>Evidence-gated metrical interpretation. Factors describe observed
/// pulse rates, not a verified time signature, downbeat or musical notation.</summary>
internal static class RhythmMetricHierarchy
{
    internal readonly record struct Root(double Bpm, bool Triple, float Strength);

    public static Root? Apply(float[,] raw, float[,] periodicity, float[,] phase,
        float[,] ranking, double minimum, double resolution, CancellationToken token)
    {
        int windows = raw.GetLength(0), candidates = raw.GetLength(1);
        var average = new float[candidates];
        var correlation = new float[candidates];
        for (int c = 0; c < candidates; c++)
        {
            token.ThrowIfCancellationRequested();
            for (int w = 0; w < windows; w++)
            {
                average[c] += raw[w, c] / windows;
                correlation[c] += periodicity[w, c] / windows;
            }
        }
        int Index(double bpm) => (int)Math.Round((bpm - minimum) / resolution);
        bool Valid(int c) => c >= 0 && c < candidates;
        float Around(float[] data, int c, int radius = 2)
        {
            if (!Valid(c)) return 0;
            float value = 0;
            for (int i = Math.Max(0, c - radius); i <= Math.Min(candidates - 1, c + radius); i++)
                value = Math.Max(value, data[i]);
            return value;
        }

        // A slow root needs evidence of its own, not just a high-frequency alias.
        // Require two supported rates for duple, three for triple. Keep the prior
        // small for ambiguous music; strong faster pulses remain possible.
        float peak = average.Max();
        Root? root = null;
        for (int c = 0; c < candidates; c++)
        {
            double bpm = minimum + c * resolution;
            if (bpm < 55 || bpm > 120) continue;
            int twice = Index(2 * bpm), triple = Index(3 * bpm);
            if (!Valid(twice)) continue;
            float a = average[c], q = correlation[c];
            float a2 = Around(average, twice, 3), q2 = Around(correlation, twice, 3);
            float a3 = Around(average, triple, 4), q3 = Around(correlation, triple, 4);
            bool duple = q >= .105f && q2 >= .10f && q >= .85f * q2 &&
                a >= .84f * a2 && a2 >= .16f && a >= .72f * peak;
            bool ternary = q >= .105f && q2 >= .12f && q3 >= .12f &&
                a >= .70f * Math.Max(a2, a3) && a >= .70f * peak &&
                a2 >= .16f && a3 >= .16f;
            if (!duple && !ternary) continue;
            float strength = a + .6f * q + .35f * Math.Min(a2, a3 > .1f ? a3 : a2)
                + (ternary ? .055f : 0);
            if (root is null || strength > root.Value.Strength)
                root = new(bpm, ternary, strength);
        }
        if (root is not { } chosen) return null;

        // Compare neighborhoods rather than exact rounded BPMs. Apply a bounded
        // prior only where the root itself has local rhythmic evidence.
        for (int w = 0; w < windows; w++)
        {
            token.ThrowIfCancellationRequested();
            int center = Index(chosen.Bpm);
            float localRaw = Max(raw, w, center, 3);
            float localPeriod = Max(periodicity, w, center, 3);
            if (localRaw < .12f || localPeriod < .085f) continue;
            float gate = Math.Clamp((localPeriod - .065f) / .09f, 0, 1);
            for (int c = 0; c < candidates; c++)
            {
                double bpm = minimum + c * resolution;
                double ratio = bpm / chosen.Bpm;
                if (Math.Abs(ratio - 1) <= .035 && raw[w, c] >= .12f)
                    ranking[w, c] += (chosen.Triple ? .17f : .12f) * gate;
                else if (Math.Abs(ratio - 2) <= .05 ||
                    chosen.Triple && (Math.Abs(ratio - 3) <= .07 ||
                        Math.Abs(ratio - 1.5) <= .045 || Math.Abs(ratio - 4d / 3) <= .045))
                {
                    if (raw[w, c] >= .55f && phase[w, c] >= .65f) continue;
                    ranking[w, c] -= (chosen.Triple ? .085f : .065f) * gate;
                }
            }
        }
        return chosen;
    }

    public static int SubdivisionAt(float[,] raw, float[,] periodicity, int window,
        double bpm, double minimum, double resolution)
    {
        int Index(double value) => (int)Math.Round((value - minimum) / resolution);
        int center = Index(bpm), n = raw.GetLength(1);
        if (center < 0 || center >= n) return 1;
        float baseline = Max(periodicity, window, center, 2);
        if (baseline < .075f) return 1;
        int selected = 1;
        float best = 0;
        for (int factor = 2; factor <= 4; factor++)
        {
            int related = Index(bpm * factor);
            if (related >= n) break;
            float q = Max(periodicity, window, related, factor + 1);
            float a = Max(raw, window, related, factor + 1);
            if (q < .12f || q < .65f * baseline || a < .16f) continue;
            float score = .65f * q + .35f * a - .015f * (factor - 2);
            if (score > best) { best = score; selected = factor; }
        }
        return selected;
    }

    private static float Max(float[,] data, int w, int center, int radius)
    {
        float result = 0;
        for (int c = Math.Max(0, center - radius); c <= Math.Min(data.GetLength(1) - 1, center + radius); c++)
            result = Math.Max(result, data[w, c]);
        return result;
    }
}
