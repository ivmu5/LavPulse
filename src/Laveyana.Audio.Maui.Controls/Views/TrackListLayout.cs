namespace Laveyana.Audio.Maui.Controls;

internal static class TrackListLayout
{
    public const int RowHeight = 54;
    public const int Stride = RowHeight + 5;
    private const int OverscanRows = 2;

    public static double RowsHeight(int count) =>
        count == 0 ? RowHeight : (double)(count - 1) * Stride + RowHeight;

    public static (int First, int End) RealizedRange(int count, double scroll, double viewport)
    {
        int first = Math.Min(count, Math.Max(0, (int)(scroll / Stride) - OverscanRows));
        int end = Math.Clamp((int)Math.Ceiling((scroll + viewport) / Stride) + OverscanRows, first, count);
        return (first, end);
    }

    public static double PaddingToKeepOffset(int count, double scroll, double viewport) =>
        scroll > 0 ? Math.Ceiling(Math.Max(0, scroll + viewport - RowsHeight(count))) : 0;
}
