using System.Diagnostics;

namespace Laveyana.Audio.Maui.Controls;

internal static class QueueViewport
{
    internal static bool CanFollow(long? lastInteraction, long now) =>
        lastInteraction is null || Stopwatch.GetElapsedTime(lastInteraction.Value, now) >= TimeSpan.FromSeconds(10);

    internal static double ContentHeight(double rowsHeight, double viewport, double rowHeight, bool alignToTop) =>
        rowsHeight + (alignToTop ? Math.Max(0, viewport - rowHeight) : 0);

    internal static double TrackOffset(int index, double stride, double rowHeight, double viewport,
        double contentHeight, bool center)
    {
        double top = index * stride;
        double inset = center ? Math.Max(0, (viewport - rowHeight) / 2) : 0;
        return Math.Clamp(top - inset, 0, Math.Max(0, contentHeight - viewport));
    }
}
