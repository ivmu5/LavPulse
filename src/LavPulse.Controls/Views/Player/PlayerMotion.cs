namespace Laveyana.LavPulse.Controls;

/// <summary>Clamped ease-in/ease-out shared by deck, cover and queue motion on the display clock.</summary>
internal static class PlayerMotion
{
    public const double OverlayOpenSeconds = .12;
    public const int TrackHandoffMilliseconds = 280;
    public const double TrackHandoffSeconds = TrackHandoffMilliseconds / 1000d;

    public static double SwipeTint(double distance, double secondThreshold) =>
        secondThreshold > 0 ? Ease(distance / secondThreshold) : 0;

    public static double OverlayReveal(double elapsed)
    {
        double t = Math.Clamp(elapsed / OverlayOpenSeconds, 0, 1);
        return 1 - (1-t) * (1-t) * (1-t);
    }

    public static double Ease(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
