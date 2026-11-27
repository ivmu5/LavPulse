namespace Laveyana.Audio.Playback;

/// <summary>Resolve octave ambiguity only when independent measured evidence
/// favors a sustained alternative. Never create confidence from the ranking prior.</summary>
internal static class RhythmTempoContinuity
{
    private readonly record struct Evidence(double Raw, double Periodicity, double Coverage);

    public static void Resolve(int[] path, float[,] raw, float[,] periodicity, float[,] phase,
        double minimum, double resolution, CancellationToken token)
    {
        if (path.Length < 6) return;
        int candidates = raw.GetLength(1);
        var counts = new int[candidates];
        foreach (int c in path) counts[c]++;
        int dominant = Array.IndexOf(counts, counts.Max());
        double Bpm(int c) => minimum + c * resolution;
        double center = Bpm(dominant);
        bool Near(double value, double target) => Math.Abs(value - target) <= target * .035;
        int familyWindows = path.Count(c => Near(Bpm(c), center));
        if (familyWindows < path.Length * .40) return;

        // Only correct a clearly supported lower alias or a very fast
        // subdivision. Ordinary ambiguous half-time/double-time stays intact.
        bool promote = center >= 55 && center < 80;
        bool demote = center >= 210;
        if (!promote && !demote) return;
        double alternative = promote ? center * 2 : center / 2;
        if (alternative < minimum || alternative > Bpm(candidates - 1)) return;

        int Best(int w, double target)
        {
            int first = Math.Max(0, (int)Math.Ceiling((target * .965 - minimum) / resolution));
            int last = Math.Min(candidates - 1, (int)Math.Floor((target * 1.035 - minimum) / resolution));
            int best = Math.Clamp((int)Math.Round((target - minimum) / resolution), 0, candidates - 1);
            float score = float.NegativeInfinity;
            for (int c = first; c <= last; c++)
            {
                float value = raw[w, c] + .5f * periodicity[w, c];
                if (value > score) { score = value; best = c; }
            }
            return best;
        }

        bool Supported(int w, int c) => raw[w,c] >= .15f &&
            periodicity[w,c] >= .105f && phase[w,c] >= .10f;
        Evidence Measure(double target)
        {
            double a = 0, q = 0;
            int support = 0, count = 0;
            for (int w = 0; w < path.Length; w++)
            {
                token.ThrowIfCancellationRequested();
                // A genuine unrelated tempo region cannot vote on this family.
                if (!Near(Bpm(path[w]), center) && !Near(Bpm(path[w]), alternative)) continue;
                int c = Best(w, target);
                a += raw[w,c];
                q += periodicity[w,c];
                if (Supported(w,c)) support++;
                count++;
            }
            return count == 0 ? default : new(a/count, q/count, support/(double)count);
        }

        var old = Measure(center);
        var other = Measure(alternative);
        bool accepted = promote
            ? other.Raw >= 1.10 * old.Raw && other.Periodicity >= 1.15 * old.Periodicity &&
                other.Coverage >= .55 && other.Coverage >= old.Coverage + .08
            : other.Raw >= .42 * old.Raw && other.Periodicity >= 1.04 * old.Periodicity &&
                other.Coverage >= .48 && other.Coverage >= old.Coverage * .90;
        if (!accepted) return;

        // A fixed reference is useful for quiet windows, but a local peak
        // remains free to vary. The caller still computes confidence and
        // eligibility at the actual selected candidate.
        for (int w = 0; w < path.Length; w++)
        {
            token.ThrowIfCancellationRequested();
            if (!Near(Bpm(path[w]), center)) continue;
            path[w] = Best(w, alternative);
        }
    }

    /// <summary>Join a single weak window between two supported windows only
    /// when its own phase and band periodicity support the same tempo.</summary>
    public static void RecoverSingleWindows(double[] tempos, float[] quality, bool[] eligible,
        float[,] raw, float[,] periodicity, float[,] phase, int[] hits,
        double minimum, double resolution, CancellationToken token)
    {
        // Read an immutable eligibility snapshot so two weak windows cannot
        // bootstrap one another into a larger unsupported region.
        var original = (bool[])eligible.Clone();
        int count = raw.GetLength(1);
        for (int w = 1; w + 1 < tempos.Length; w++)
        {
            token.ThrowIfCancellationRequested();
            if (original[w] || !original[w-1] || !original[w+1] || hits[w] < 8) continue;
            double target = (tempos[w-1] + tempos[w+1]) / 2;
            if (Math.Abs(tempos[w-1] - tempos[w+1]) > Math.Max(1.5, target * .025)) continue;
            int middle = (int)Math.Round((target - minimum) / resolution);
            int best = -1;
            float value = 0;
            for (int c = Math.Max(0, middle-3); c <= Math.Min(count-1, middle+3); c++)
            {
                if (raw[w,c] < .14f || periodicity[w,c] < .115f || phase[w,c] < .09f) continue;
                float score = raw[w,c] + .5f * periodicity[w,c];
                if (score > value) { best = c; value = score; }
            }
            if (best < 0) continue;
            tempos[w] = minimum + best * resolution;
            quality[w] = raw[w,best];
            eligible[w] = true;
        }
    }
}
