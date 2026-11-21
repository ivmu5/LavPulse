namespace Laveyana.LavPulse.Controls;

/// <summary>Captures pointer input so a waveform can cancel when the pointer leaves upward.</summary>
internal static partial class TimelineInput
{
#if !ANDROID && !WINDOWS && !IOS
    public static void Attach(GraphicsView view, Func<PointF, bool> begin, Action<PointF> move, Action<PointF> end, Action cancel)
    {
        view.StartInteraction += (_, e) => { if (e.Touches.Length > 0) begin(e.Touches[0]); };
        view.DragInteraction += (_, e) => { if (e.Touches.Length > 0) move(e.Touches[0]); };
        view.EndInteraction += (_, e) => { if (e.Touches.Length > 0) end(e.Touches[0]); else cancel(); };
        view.CancelInteraction += (_, _) => cancel();
    }
#endif
}
