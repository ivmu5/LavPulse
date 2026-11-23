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
        CheckMotionCadence();
        Console.WriteLine("OK: spectrum frame limits, fractional cadence, and FPS-synchronized transition curves.");
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
