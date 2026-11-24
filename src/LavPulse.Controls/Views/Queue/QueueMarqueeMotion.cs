namespace Laveyana.LavPulse.Controls;

/// <summary>Single-line, horizontal-only marquee geometry shared by all queue rows.</summary>
internal static class QueueMarqueeMotion
{
    private const double Speed = 18;
    private const double EndpointPauseSeconds = 1.2;

    /// <summary>Moves between both ends without changing a line's vertical position.</summary>
    public static float Offset(float overflow, double seconds)
    {
        if (overflow <= 0 || !float.IsFinite(overflow) || !double.IsFinite(seconds)) return 0;
        double travel = overflow / Speed;
        double cycle = 2 * travel + 2 * EndpointPauseSeconds;
        double phase = seconds % cycle;
        if (phase < 0) phase += cycle;
        return (float)Math.Clamp(
            Math.Min(phase - EndpointPauseSeconds, cycle - phase) * Speed, 0, overflow);
    }

    /// <summary>Fixed glyph baseline, independent of the title's length and scroll offset.</summary>
    public static float Baseline(float centerY, float fontSize) => centerY + fontSize * .35f;
}
