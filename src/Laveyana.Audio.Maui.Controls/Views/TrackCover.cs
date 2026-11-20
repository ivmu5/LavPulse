using System.Runtime.InteropServices;

namespace Laveyana.Audio.Maui.Controls;

internal static class TrackCover
{
    private static readonly Point[] Corners = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    /// <summary>Provides each image decode with a fresh stream over existing artwork bytes.</summary>
    public static ImageSource? FromArtwork(ReadOnlyMemory<byte> artwork)
    {
        if (artwork.IsEmpty) return null;
        var bytes = MemoryMarshal.TryGetArray(artwork, out var segment) ? segment : new ArraySegment<byte>(artwork.ToArray());
        return ImageSource.FromStream(() => new System.IO.MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false));
    }

    /// <summary>Chooses a stable diagonal for the same track in the queue and on either deck.</summary>
    public static LinearGradientBrush CreateGradient(PlayerColors colors, string? fileName)
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
