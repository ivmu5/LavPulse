using Laveyana.LavPulse.Controls;

internal static class SpectrumFrameClockChecks
{
    public static void Run()
    {
        Require(Count(refresh: 120, limit: 0) == 1200, "Automatic must accept every display tick.");
        Require(Count(refresh: 120, limit: 30) == 300, "30 FPS must schedule every fourth 120 Hz tick.");
        Require(Count(refresh: 120, limit: 90) == 900, "90 FPS on a 120 Hz display lost the fractional cadence.");
        Require(Count(refresh: 144, limit: 60) == 600, "60 FPS on a 144 Hz display lost the fractional cadence.");
        Require(Count(refresh: 240, limit: 165) == 1650, "165 FPS on a 240 Hz display lost the fractional cadence.");
        Require(Count(refresh: 60, limit: 120) == 600, "The limit must not skip ticks on a slower display.");

        SpectrumFrameClock clock = default;
        Require(clock.TryAdvance(1, 60, out _), "The first frame was skipped.");
        Require(!clock.TryAdvance(1, 60, out _), "A duplicate display tick produced another drawing.");
        Require(clock.TryAdvance(2, 60, out float elapsed) && elapsed == .1f, "A long stall should clamp visual smoothing.");
        Require(!clock.TryAdvance(2.001, 60, out _), "A stall produced catch-up drawings.");
        Require(clock.TryAdvance(2.017, 60, out elapsed) && Math.Abs(elapsed - .017f) < .0001f,
            "Smoothing must use the actual time since the last draw.");
        SpectrumFrameClock guarded = default;
        Require(!guarded.TryAdvance(double.NaN,60,out _) &&
            !guarded.TryAdvance(double.PositiveInfinity,60,out _) &&
            !guarded.TryAdvance(-1,60,out _) &&
            !guarded.TryAdvance(1,-1,out _), "Invalid display inputs poisoned the clock.");
        Require(guarded.TryAdvance(1,30,out _) &&
            guarded.TryAdvance(1.001,0,out _) &&
            guarded.TryAdvance(1.002,0,out _) &&
            guarded.TryAdvance(1.003,30,out _),
            "Automatic cadence retained an obsolete FPS deadline.");
        CheckMotionCadence();
        CheckLongRunningCadence();
        CheckWaveformDynamics();
        Console.WriteLine("OK: spectrum frame limits, FPS-synchronized motion and waveform transient contrast.");
    }

    private static void CheckMotionCadence()
    {
        Require(PlayerMotion.Ease(-1) == 0 && PlayerMotion.Ease(0) == 0 &&
            PlayerMotion.Ease(1) == 1 && PlayerMotion.Ease(2) == 1,
            "A transition must clamp to its endpoints.");
        foreach (int rate in new[] { 30, 60, 120 })
        {
            SpectrumFrameClock clock = default;
            double previous = 0;
            int frames = 0;
            for (int tick = 0; tick <= 120; tick++)
            {
                double time = tick / 120d;
                if (!clock.TryAdvance(time, rate, out _)) continue;
                double fraction = PlayerMotion.Ease(Math.Min(1, time / .46));
                Require(fraction >= previous, "A transition moved backward between display frames.");
                previous = fraction;
                frames++;
            }
            Require(previous == 1 && frames >= rate, "The transition did not finish at the selected display cadence.");
        }
    }

    private static void CheckLongRunningCadence()
    {
        foreach (int limit in new[] { 30, 60, 90, 120, 144, 165, 240, 360 })
        {
            const int refresh = 360;
            const int seconds = 120;
            SpectrumFrameClock clock = default;
            int count = 0;
            double start = 24 * 60 * 60; // long-running monotonic clock, not a fresh application
            for (int tick = 0; tick < refresh * seconds; tick++)
            {
                double timestamp = start + tick / (double)refresh;
                if (clock.TryAdvance(timestamp, limit, out _)) count++;
                Require(!clock.TryAdvance(timestamp, limit, out _),
                    "A duplicate compositor callback rendered a second frame.");
            }
            Require(Math.Abs(count - limit * seconds) <= 1,
                $"A long-running {limit} FPS animation drifted from its configured cadence: {count}.");
        }

        SpectrumFrameClock reset = default;
        Require(reset.TryAdvance(30, 120, out _), "Initial display callback was lost.");
        reset = default;
        Require(reset.TryAdvance(30.001, 30, out _), "Changing the FPS limit retained an obsolete deadline.");
        Require(!reset.TryAdvance(30.010, 30, out _), "The new FPS limit was not applied.");
    }

    private static void CheckWaveformDynamics()
    {
        var points = Enumerable.Repeat(new Laveyana.Audio.Playback.WaveformPoint(.25f, .10f, .10f, .08f), 16).ToArray();
        points[7] = new(.92f, .76f, .15f, .12f);
        var dynamics = new WaveformDynamics(points);
        Require(dynamics.BassAttack(7) > .8f && dynamics.BassAttack(12) == 0,
            "A low-frequency impact was not distinguished from stable material.");
        Require(dynamics.Height(points[7].Peak, dynamics.BassAttack(7)) >
                2 * dynamics.Height(points[4].Peak, dynamics.BassAttack(4)),
            "The detailed waveform suppressed the amplitude contrast of drum transients.");
        var stable = new WaveformDynamics(Enumerable.Repeat(new Laveyana.Audio.Playback.WaveformPoint(
            .5f, .15f, .2f, .1f), 25).ToArray());
        Require(stable.BassAttack(18) == 0 && stable.Height(.5f, 0) <= 1f,
            "A steady bass tone was incorrectly marked as a new transient.");
    }

    private static int Count(int refresh, int limit)
    {
        SpectrumFrameClock clock = default;
        int frames = 0;
        for (int i = 0; i < refresh * 10; i++)
        {
            if (clock.TryAdvance(i / (double)refresh, limit, out _)) frames++;
        }
        return frames;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
