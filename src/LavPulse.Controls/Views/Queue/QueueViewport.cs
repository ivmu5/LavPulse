using System.Diagnostics;

namespace Laveyana.LavPulse.Controls;

internal static class QueueViewport
{
    internal static bool CanFollow(long? lastInteraction, long now) =>
        lastInteraction is null || Stopwatch.GetElapsedTime(lastInteraction.Value, now) >= TimeSpan.FromSeconds(10);

    internal static double EndPadding(double viewport, double rowHeight) => Math.Max(0, viewport - rowHeight);

    internal static double TrackOffset(int index, double stride, double rowHeight, double viewport,
        double contentHeight, bool center)
    {
        double top = index * stride;
        double inset = center ? Math.Max(0, (viewport - rowHeight) / 2) : 0;
        return Math.Clamp(top - inset, 0, Math.Max(0, contentHeight - viewport));
    }
}
