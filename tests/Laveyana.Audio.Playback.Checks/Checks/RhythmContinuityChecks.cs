using Laveyana.Audio.Playback;

internal static partial class Program
{
    private static void CheckRhythmContinuity()
    {
        const int windows = 12, candidates = 401;
        int Index(double bpm) => (int)((bpm - 40) * 2);
        var raw = new float[windows,candidates];
        var periodicity = new float[windows,candidates];
        var phase = new float[windows,candidates];
        void Set(int w, double bpm, float a, float q)
        {
            int c = Index(bpm);
            raw[w,c] = a;
            periodicity[w,c] = q;
            phase[w,c] = .25f;
        }
        var path = Enumerable.Repeat(Index(64), windows).ToArray();
        for (int w = 0; w < windows; w++)
        {
            Set(w, 64, .18f, .12f);
            Set(w, 128, .23f, .17f);
        }
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(128)), "A supported doubled beat did not replace its slow alias.");
        Array.Fill(path, Index(64));
        for (int w = 0; w < windows; w++) Set(w, 128, .25f, .04f);
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(64)), "Weak harmonic evidence overwrote a genuine slow beat.");
        Array.Clear(raw); Array.Clear(periodicity); Array.Clear(phase);
        Array.Fill(path, Index(226));
        for (int w = 0; w < windows; w++)
        {
            Set(w, 226, .40f, .10f);
            Set(w, 113, .19f, .14f);
        }
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(113)), "A corroborated slower pulse did not replace its high subdivision.");
        Array.Fill(path, Index(226));
        for (int w = 0; w < windows; w++) Set(w, 113, .19f, .04f);
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(226)), "An independently unsupported half tempo overwrote a fast beat.");
        var tempos = Enumerable.Repeat(100d, windows).ToArray();
        var quality = Enumerable.Repeat(.3f, windows).ToArray();
        var eligible = Enumerable.Repeat(true, windows).ToArray();
        var hits = Enumerable.Repeat(12, windows).ToArray();
        eligible[3] = eligible[6] = eligible[7] = eligible[10] = false;
        Set(3, 100, .145f, .13f);
        Set(6, 100, .145f, .13f);
        Set(7, 100, .145f, .13f);
        Set(10, 100, .145f, .02f);
        RhythmTempoContinuity.RecoverSingleWindows(tempos, quality, eligible, raw, periodicity, phase, hits, 40, .5, default);
        Require(eligible[3] && quality[3] == .145f && !eligible[6] && !eligible[7] && !eligible[10],
            "Recovery inflated confidence, chained weak windows, or crossed unsupported audio.");
        // 150 BPM, 100 Hz envelope, eight-hop offset. The old phase
        // search never tested this tempo because it was above 120 BPM.
        var energy = Enumerable.Range(0, 1000).Select(i =>
            new RhythmEnergy(i % 40 == 8 ? 1f : 0f, i % 40 == 8 ? .5f : 0f, 0)).ToArray();
        var evidence = new RhythmBandEvidence(energy, 100, default);
        var refined = evidence.ChoosePhase(0, 40, 150, 0, 1000, false);
        Require(refined.Alternate && refined.SelectedScore > refined.OriginalScore &&
            refined.Phase >= 5 && refined.Phase <= 10,
            "An independently supported fast phase offset was not selected.");
        Require(!evidence.ChoosePhase(0, 40, 150, 0, 1000, true).Alternate,
            "A strong phase anchor was overwritten.");
        CheckBassOctave();
        Console.WriteLine("OK: evidence-gated octave correction, isolated window recovery and local phase refinement.");
    }

    private static void CheckBassOctave()
    {
        const int rate = 100, length = 3200, windows = 12, stride = 200, radius = 450;
        int Index(double bpm) => (int)((bpm - 40) * 2);
        RhythmBandEvidence Build(double alternateBass, bool subdivision = false, bool steadyBass = false)
        {
            var energy = new RhythmEnergy[length];
            if (steadyBass) Array.Fill(energy, new RhythmEnergy(.4f, 0, 0));
            for (int beat = 0; ; beat++)
            {
                int i = (int)Math.Round(11 + beat * rate * 60d / 172 + Math.Sin(beat * 3) * 2);
                if (i >= length) break;
                float bass = steadyBass ? .4f : beat % 2 == 0 ? 1f : (float)alternateBass;
                energy[i] = new(bass, .25f, .8f);
                if (subdivision && i + 17 < length) energy[i + 17] = new(1, .25f, .8f);
            }
            return new RhythmBandEvidence(energy, rate, default);
        }
        var strong = Build(.64);
        Require(strong.MeasureBassPulse(600, 1500, 172, default) > 0,
            "Alternating independent bass attacks at 172 BPM were missed.");
        Require(Build(0).MeasureBassPulse(600, 1500, 172, default) == 0,
            "High-band subdivisions doubled a genuine 86 BPM bass pulse.");
        Require(Build(.04).MeasureBassPulse(600, 1500, 172, default) == 0,
            "Weak offbeat bass subdivisions doubled the tempo.");
        Require(Build(1, subdivision: true).MeasureBassPulse(600, 1500, 172, default) == 0,
            "Dense off-grid bass attacks were treated as a confirmed fast beat.");
        Require(Build(0, steadyBass: true).MeasureBassPulse(600, 1500, 172, default) == 0,
            "Sustained bass was treated as repeated attacks.");

        var raw = new float[windows,401];
        var periodicity = new float[windows,401];
        var phase = new float[windows,401];
        for (int w = 0; w < windows; w++)
        {
            raw[w,Index(86)] = .40f; periodicity[w,Index(86)] = .25f;
            raw[w,Index(172)] = .35f; periodicity[w,Index(172)] = .20f;
            phase[w,Index(172)] = .30f;
        }
        var path = Enumerable.Repeat(Index(86), windows).ToArray();
        path[8] = Index(128);
        RhythmBassOctave.Resolve(path, raw, periodicity, phase, strong,
            stride, radius, 40, .5, default);
        Require(path[4] == Index(172) && path[8] == Index(128),
            "Bass parity did not resolve the alias or changed an unrelated tempo.");
        Array.Fill(path, Index(86));
        RhythmBassOctave.Resolve(path, raw, periodicity, phase, Build(0),
            stride, radius, 40, .5, default);
        Require(path.All(c => c == Index(86)), "A genuine slow tempo was doubled.");
        Array.Fill(path, Index(86));
        for (int w = 0; w < windows; w++)
            if (w != 5) periodicity[w,Index(172)] = 0;
        RhythmBassOctave.Resolve(path, raw, periodicity, phase, strong,
            stride, radius, 40, .5, default);
        Require(path.All(c => c == Index(86)), "An isolated fast candidate changed the tempo.");
    }
}
