namespace Laveyana.Audio.Playback;

/// <summary>Resolves octave ambiguity using corroborated half-period evidence.</summary>
internal static class RhythmTempoFamily
{
    public static void Resolve(float[,] scores, float[,] periodicity, float[,] phase, double minimum, double resolution)
    {
        int windows = scores.GetLength(0), candidates = scores.GetLength(1);
        for (int fast = candidates - 1; fast >= 0; fast--)
        {
            double bpm = minimum + fast * resolution;
            if (bpm < 120) break;
            int half = (int)Math.Round((bpm / 2 - minimum) / resolution);
            if (half < 0 || half >= fast) continue;
            int credible = 0, active = 0;
            for (int w = 0; w < windows; w++)
            {
                if (scores[w, fast] < .28f) continue;
                active++;
                if (periodicity[w, half] >= .16f && periodicity[w, half] >= .75f * periodicity[w, fast] &&
                    phase[w, half] >= .17f) credible++;
            }
            bool globalOctave = bpm >= 160 && active >= 4 && credible >= Math.Max(3, .40 * active);
            for (int w = 0; w < windows; w++)
            {
                float high = scores[w, fast], low = scores[w, half];
                if (high < .28f) continue;
                bool localOctave = periodicity[w, half] >= .16f &&
                    periodicity[w, half] >= .75f * periodicity[w, fast] && phase[w, half] >= .17f;
                bool nearTie = periodicity[w, half] >= .18f &&
                    periodicity[w, half] >= 1.05f * periodicity[w, fast] &&
                    low >= .86f * high && phase[w, half] >= .15f;
                if ((bpm >= 160 && (localOctave || globalOctave && periodicity[w, half] >= .12f &&
                    periodicity[w, half] >= .60f * periodicity[w, fast])) || (bpm < 160 && nearTie))
                    scores[w, half] = Math.Min(1, Math.Max(low, high + .018f));
            }
        }
    }
}
