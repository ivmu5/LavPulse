namespace Laveyana.Audio.Playback;

public static partial class TrackRhythmAnalyzer
{
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
