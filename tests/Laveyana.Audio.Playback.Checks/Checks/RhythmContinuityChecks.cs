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
        Console.WriteLine("OK: evidence-gated octave correction and isolated window recovery.");
    }
}
