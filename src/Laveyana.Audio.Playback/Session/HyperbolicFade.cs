namespace Laveyana.Audio.Playback;

internal static class HyperbolicFade
{
    private const float Curvature = 2.5f;

    /// <summary>Returns mirrored gain coefficients with constant combined power across a transition.</summary>
    public static (float Incoming, float Outgoing) Gains(float progress)
    {
        float incoming = Weight(progress);
        float outgoing = Weight(1 - progress);
        float scale = 1 / MathF.Sqrt(incoming * incoming + outgoing * outgoing);
        return (incoming * scale, outgoing * scale);
    }

    /// <summary>Shifts more of the gain change toward the middle of the fade.</summary>
    private static float Weight(float progress) => progress / (Curvature - (Curvature - 1) * progress);
}
