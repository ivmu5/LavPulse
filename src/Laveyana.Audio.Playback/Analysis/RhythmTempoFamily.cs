namespace Laveyana.Audio.Playback;

/// <summary>Tempo ranking uses immutable measured evidence. A related tempo can
/// influence the ranking, but a promotion cannot propagate to another octave.</summary>
internal static class RhythmTempoFamily
{
    internal sealed record Result(float[,] Ranking, float[,] Confidence, float[] TrackPeriodicity);

    public static Result Resolve(float[,] raw, float[,] periodicity, float[,] phase,
        double minimum, double resolution, CancellationToken token = default)
    {
        int windows = raw.GetLength(0), candidates = raw.GetLength(1);
        var ranking = new float[windows, candidates];
        var confidence = (float[,])raw.Clone();
        var average = new float[candidates];
        var periodicAverage = new float[candidates];
        for (int c = 0; c < candidates; c++)
        {
            token.ThrowIfCancellationRequested();
            for (int w = 0; w < windows; w++)
            {
                average[c] += raw[w, c] / windows;
                periodicAverage[c] += periodicity[w, c] / windows;
            }
        }
        var global = new float[candidates];
        var globalPeriodic = new float[candidates];
        var peak = new int[candidates];
        for (int c = 0; c < candidates; c++)
        {
            global[c] = Max(average, c, 2);
            globalPeriodic[c] = Max(periodicAverage, c, 2);
            peak[c] = c;
            for (int j = Math.Max(0, c - 8); j <= Math.Min(candidates - 1, c + 8); j++)
                if (average[j] > average[peak[c]]) peak[c] = j;
        }

        int Index(double bpm) => (int)Math.Round((bpm - minimum) / resolution);
        double Bpm(int index) => minimum + index * resolution;
        bool Valid(int index) => index >= 0 && index < candidates;
        bool CanPreferHalf(int slow, int fast) =>
            Valid(slow) && Valid(fast) && Bpm(peak[fast]) >= 165 &&
            global[fast] >= .17f && globalPeriodic[slow] >= .085f &&
            globalPeriodic[slow] >= .65f * globalPeriodic[fast] &&
            global[slow] >= .42f * global[fast];

        Span<int> relatives = stackalloc int[4];
        Span<int> relatedRatios = stackalloc int[2];
        for (int c = 0; c < candidates; c++)
        {
            token.ThrowIfCancellationRequested();
            double bpm = Bpm(c);
            int half = Index(bpm / 2), twice = Index(bpm * 2), quarter = Index(bpm / 4);
            relatives[0] = quarter; relatives[1] = half;
            relatives[2] = twice; relatives[3] = Index(bpm * 4);
            relatedRatios[0] = Index(bpm * 1.5); relatedRatios[1] = Index(bpm * (2d / 3));
            float family = global[c];
            bool barAlias = false;
            foreach (int j in relatives)
            {
                if (!Valid(j)) continue;
                family = Math.Max(family, global[j]);
                if (bpm < 55 && j > c && global[j] >= .16f &&
                    global[j] >= .70f * global[c] && globalPeriodic[j] >= .55f * globalPeriodic[c])
                    barAlias = true;
            }
            bool preferSlower = bpm >= 157 && CanPreferHalf(half, c);
            bool preferThisHalf = bpm >= 78.5 && bpm <= 120 && CanPreferHalf(c, twice);
            bool supportedHalf = bpm >= 60 && bpm < 82 && Valid(twice) &&
                global[c] >= .86f * global[twice] && globalPeriodic[c] >= .90f * globalPeriodic[twice] &&
                globalPeriodic[c] >= .12f;
            bool preferBeatOverBar = bpm >= 115 && bpm <= 165 && Valid(quarter) &&
                global[quarter] >= .16f && globalPeriodic[quarter] >= .085f &&
                global[c] >= .72f * global[quarter] && globalPeriodic[c] >= .55f * globalPeriodic[quarter];

            for (int w = 0; w < windows; w++)
            {
                float local = raw[w, c];
                float localPeriodic = Max(periodicity, w, c, 2);
                // Ranking priors are deliberately not reported as measured confidence.
                float score = local + .12f * global[c] + .04f * family + .12f * localPeriodic;
                // A 3:2 relationship may represent triplets, not a new tempo.
                // Penalize only the weaker candidate when the other pulse has
                // both better local periodicity and better original evidence.
                foreach (int related in relatedRatios)
                {
                    if (!Valid(related)) continue;
                    float relatedPeriodic = Max(periodicity, w, related, 2);
                    float relatedRaw = Max(raw, w, related, 2);
                    if (relatedPeriodic >= .13f && relatedPeriodic > 1.4f * localPeriodic &&
                        relatedRaw > local + .035f)
                        score -= .045f;
                }
                if (barAlias) score -= .18f;
                if (preferSlower)
                {
                    float gate = Math.Clamp((Max(periodicity, w, half, 2) - .035f) / .09f, 0, 1);
                    score -= .20f * gate;
                }
                if (preferThisHalf)
                {
                    float gate = Math.Clamp((localPeriodic - .055f) / .09f, 0, 1);
                    float other = Max(raw, w, twice, 1);
                    float otherPeriodic = Max(periodicity, w, twice, 2);
                    // Do not promote an unrelated octave when the doubled
                    // pulse has no credible evidence in this local window.
                    if (otherPeriodic < .08f || other < .16f) gate *= .20f;
                    score += Math.Min(.22f, Math.Max(0, other - local + .02f) + .065f) * gate;
                    // A subdivision confirms a family, not the phase of its slower pulse.
                    if (phase[w, c] >= .10f)
                        confidence[w, c] = Math.Max(local, .75f * other * gate);
                }
                if (supportedHalf) score += .045f * Math.Clamp(localPeriodic / .13f, 0, 1);
                if (preferBeatOverBar) score += .10f * Math.Clamp(localPeriodic / .16f, 0, 1);
                ranking[w, c] = score;
            }
        }
        return new(ranking, confidence, globalPeriodic);
    }

    private static float Max(float[] values, int index, int radius)
    {
        float result = 0;
        for (int j = Math.Max(0, index - radius); j <= Math.Min(values.Length - 1, index + radius); j++)
            result = Math.Max(result, values[j]);
        return result;
    }

    private static float Max(float[,] values, int window, int index, int radius)
    {
        float result = 0;
        for (int j = Math.Max(0, index - radius); j <= Math.Min(values.GetLength(1) - 1, index + radius); j++)
            result = Math.Max(result, values[window, j]);
        return result;
    }
}
