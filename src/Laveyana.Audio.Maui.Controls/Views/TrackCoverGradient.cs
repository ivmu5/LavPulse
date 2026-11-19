namespace Laveyana.Audio.Maui.Controls;

internal static class TrackCoverGradient
{
    private static readonly Point[] Corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    /// <summary>Chooses a stable diagonal for the same track in the queue and on either deck.</summary>
    public static LinearGradientBrush Create(PlayerColors colors, string? fileName)
    {
        uint hash = 2166136261;
        if (fileName is not null)
        {
            foreach (char character in fileName)
            {
                hash = unchecked((hash ^ character) * 16777619);
            }
        }
        int corner = fileName is null ? 0 : (int)(hash % Corners.Length);
        return new LinearGradientBrush(
            [new(colors.WaveLow.WithAlpha(.65f), 0), new(colors.WaveMid.WithAlpha(.5f), .5f), new(colors.Alternate.WithAlpha(.7f), 1)],
            Corners[corner], Corners[(corner + 2) % Corners.Length]);
    }
}
