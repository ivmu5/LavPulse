namespace Laveyana.LavPulse.Controls;

/// <summary>Schedules spectrum updates on display ticks, including rates that do not divide the refresh rate.</summary>
internal struct SpectrumFrameClock
{
    private const double EarlyToleranceSeconds = .0002;
    private double? _lastDraw;
    private double? _nextDraw;

    public bool TryAdvance(double timestamp, int framesPerSecond, out float elapsed)
    {
        elapsed = 0;
        // A bad native timestamp must not poison all future deadlines.
        if (!double.IsFinite(timestamp) || timestamp < 0 || framesPerSecond < 0) return false;
        if ((_lastDraw is double last && timestamp <= last) ||
            (framesPerSecond > 0 && _nextDraw is double next && timestamp + EarlyToleranceSeconds < next))
        {
            return false;
        }

        elapsed = _lastDraw is double previous
            ? (float)Math.Min(timestamp - previous, .1)
            : 1f / Math.Max(framesPerSecond, 60);
        _lastDraw = timestamp;

        if (framesPerSecond > 0)
        {
            double interval = 1d / framesPerSecond;
            // Keep the deadline on its original cadence and discard missed deadlines after a long stall.
            var scheduled = _nextDraw ?? timestamp;
            _nextDraw = (timestamp - scheduled >= interval ? timestamp : scheduled) + interval;
        }
        else _nextDraw = null;
        return true;
    }
}
