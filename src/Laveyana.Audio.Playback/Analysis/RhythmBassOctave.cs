namespace Laveyana.Audio.Playback;

/// <summary>
/// Conservative local correction of half-time aliases using alternating bass
/// attacks. A repeated high-band subdivision is not sufficient evidence.
/// </summary>
internal static class RhythmBassOctave
{
    // The input is the square-root-compressed positive low-band energy flux
    // already calculated by RhythmBandEvidence, sampled at about 50 Hz.
    internal static double Measure(float[] flux, int start, int end, double rate,
        double bpm, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double period = rate * 60 / bpm;
        if (!double.IsFinite(period) || period < 5 || end - start < 12 * period)
            return 0;
        double radius = Math.Min(.14 * period, .055 * rate);
        double maximum = 0, sum = 0;
        for (int i = start; i < end; i++)
        {
            double value = (double)flux[i] * flux[i];
            maximum = Math.Max(maximum, value);
            sum += value;
        }
        // Undo the second compression, not the original energy-to-amplitude
        // transform. Adapt to local loudness and reject flat/ringing envelopes.
        double threshold = Math.Max(.12 * maximum, 3 * sum / (end - start));
        if (maximum <= 1e-12 || threshold > maximum) return 0;

        double Peak(double position)
        {
            double peak = 0;
            for (int i = Math.Max(start, (int)Math.Ceiling(position - radius));
                i <= Math.Min(end - 1, (int)Math.Floor(position + radius)); i++)
                peak = Math.Max(peak, (double)flux[i] * flux[i]);
            return peak;
        }

        double best = 0;
        for (int p = 0; p < 16; p++)
        {
            token.ThrowIfCancellationRequested();
            double even = 0, odd = 0, off = 0;
            int evenCount = 0, oddCount = 0, evenHits = 0, oddHits = 0,
                offCount = 0, offHits = 0;
            int index = 0;
            for (double t = start + radius + p * period / 16; t <= end - 1 - radius; t += period)
            {
                double value = Peak(t);
                if ((index++ & 1) == 0)
                {
                    even += value; evenCount++;
                    if (value >= threshold) evenHits++;
                }
                else
                {
                    odd += value; oddCount++;
                    if (value >= threshold) oddHits++;
                }
                if (t + period / 2 <= end - 1 - radius)
                {
                    double between = Peak(t + period / 2);
                    off += between; offCount++;
                    if (between >= threshold) offHits++;
                }
            }
            if (evenCount < 6 || oddCount < 6 || offCount == 0) continue;
            double coverage = Math.Min(evenHits / (double)evenCount, oddHits / (double)oddCount);
            double a = even / evenCount, b = odd / oddCount;
            double balance = Math.Min(a, b) / Math.Max(1e-12, Math.Max(a, b));
            double contrast = (off / offCount) / Math.Max(1e-12, Math.Min(a, b));
            if (coverage >= .75 && balance >= .60 && contrast <= .35 &&
                offHits / (double)offCount <= .35)
                best = Math.Max(best, coverage * balance * (1 - contrast));
        }
        return best;
    }

    internal static void Resolve(int[] path, float[,] raw, float[,] periodicity, float[,] phase,
        RhythmBandEvidence bands, int stride, int radius, double minimum, double resolution,
        CancellationToken token)
    {
        int candidates = raw.GetLength(1);
        var selected = new int[path.Length];
        Array.Fill(selected, -1);
        double Bpm(int c) => minimum + c * resolution;
        for (int w = 0; w < path.Length; w++)
        {
            token.ThrowIfCancellationRequested();
            double slow = Bpm(path[w]);
            // The previous continuity pass already handles lower aliases.
            // At ordinary half-time tempos require independent parity evidence.
            if (slow < 80 || slow > 120) continue;
            double target = slow * 2;
            int first = Math.Max(0, (int)Math.Ceiling((target * .985 - minimum) / resolution));
            int last = Math.Min(candidates - 1, (int)Math.Floor((target * 1.015 - minimum) / resolution));
            double best = 0;
            int center = w * stride;
            for (int c = first; c <= last; c++)
            {
                if (raw[w,c] < Math.Max(.18f, .65f * raw[w,path[w]]) ||
                    periodicity[w,c] < Math.Max(.105f, .55f * periodicity[w,path[w]]) ||
                    phase[w,c] < .16f) continue;
                double pulse = bands.MeasureBassPulse(center - radius, center + radius + 1, Bpm(c), token);
                if (pulse <= 0) continue;
                double score = pulse * (.5 + raw[w,c] + .5 * periodicity[w,c]);
                if (score > best) { best = score; selected[w] = c; }
            }
        }

        // Require at least three consecutive supported windows at compatible
        // fast tempos (overlapping windows; not three independent samples).
        // No confidence, scores or unrelated tempo windows are overwritten.
        for (int start = 0; start < selected.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (selected[start] < 0) { start++; continue; }
            int end = start + 1;
            double reference = Bpm(selected[start]);
            while (end < selected.Length && selected[end] >= 0 &&
                Math.Abs(Bpm(selected[end]) - reference) <= reference * .035) end++;
            if (end - start >= 3)
                for (int w = start; w < end; w++) path[w] = selected[w];
            start = end;
        }
    }
}
