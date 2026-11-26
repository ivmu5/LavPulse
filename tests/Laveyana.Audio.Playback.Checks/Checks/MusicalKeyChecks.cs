using Laveyana.Audio.Playback;

internal static class MusicalKeyChecks
{
    public static void Run()
    {
        var c = Estimate([60, 64, 67], [60, 65, 69], [59, 62, 67], [60, 64, 67]);
        var a = Estimate([57, 60, 64], [57, 62, 65], [56, 59, 64], [57, 60, 64]);
        Require(c is { Tonic: 0, Mode: MusicalMode.Major, Camelot: "8B" }, "C major was not identified.");
        Require(a is { Tonic: 9, Mode: MusicalMode.Minor, Camelot: "8A" }, "A minor was not identified.");
        var silence = new MusicalKeyAnalyzer(48000);
        var single = new MusicalKeyAnalyzer(48000);
        var block = new float[8192];
        for (int n = 0; n < 48; n++)
        {
            Array.Clear(block);
            silence.Add(block, default);
            for (int i = 0; i < block.Length / 2; i++)
                block[2*i] = block[2*i+1] = (float)Math.Sin(Math.Tau * 440 * (n * 4096L + i) / 48000);
            single.Add(block, default);
        }
        Require(silence.Finish() is null && single.Finish() is null, "Non-tonal evidence produced a key.");
        Console.WriteLine("OK: tonal PCM, relative major/minor, anti-phase stereo and unknown-key gates.");
    }

    private static TrackMusicalKey? Estimate(params int[][] chords)
    {
        var analyzer = new MusicalKeyAnalyzer(48000);
        var block = new float[4096 * 2];
        // Each chord is held for two analysis windows; anti-phase channels
        // deliberately exercise the packed stereo FFT.
        long frame = 0;
        foreach (var chord in chords)
            for (int n = 0; n < 16; n++)
            {
                for (int i = 0; i < 4096; i++, frame++)
                {
                    double value = 0;
                    for (int k = 0; k < chord.Length; k++)
                        value += (k == 0 ? .24 : .16) *
                            Math.Sin(Math.Tau * 440 * Math.Pow(2, (chord[k] - 69) / 12d) * frame / 48000);
                    block[2*i] = (float)value;
                    block[2*i+1] = (float)-value;
                }
                analyzer.Add(block, default);
            }
        return analyzer.Finish();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
