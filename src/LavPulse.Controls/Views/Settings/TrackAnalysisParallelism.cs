namespace Laveyana.LavPulse.Controls;

internal static class TrackAnalysisParallelism
{
    public const int Automatic = 0;
    public const int Minimum = 1;
    public const int MaximumSupported = 8;

    // Keep one logical processor's worth of capacity available for the player when possible.
    public static int MaximumFor(int processors) => Math.Clamp(processors - 1, Minimum, MaximumSupported);

    public static int Resolve(int requested, int processors) =>
        Math.Clamp(requested <= Automatic ? processors / 2 : requested, Minimum, MaximumFor(processors));
}
