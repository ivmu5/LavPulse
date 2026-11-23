namespace Laveyana.LavPulse.Controls;

/// <summary>Clamped ease-in/ease-out shared by deck, cover and queue motion on the display clock.</summary>
internal static class PlayerMotion
{
    public static double Ease(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
