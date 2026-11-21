namespace Laveyana.LavPulse.Controls;

internal static partial class TrackListSwipe
{
    private const uint Threshold = 66;

    public static void Attach(TrackListRow row, Func<object?> item, Func<bool> enabled, Action reveal)
    {
        var swipe = new SwipeGestureRecognizer { Direction = SwipeDirection.Left, Threshold = Threshold };
        swipe.Swiped += (_, _) =>
        {
            if (enabled() && item() is not null) reveal();
        };
        row.TouchBody.GestureRecognizers.Add(swipe);
        AttachMouse(row.TouchBody, item, enabled, reveal);
    }

    static partial void AttachMouse(View view, Func<object?> item, Func<bool> enabled, Action reveal);
}
