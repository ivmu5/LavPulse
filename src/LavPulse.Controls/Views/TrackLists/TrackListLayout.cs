namespace Laveyana.LavPulse.Controls;

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

    public const int MaximumPinnedParents = 3;

    // Keep room for at least one ordinary row, including in short landscape windows.
    public static int PinnedCapacity(double viewport) =>
        !double.IsFinite(viewport) || viewport < RowHeight ? 0 :
            (int)Math.Clamp(Math.Floor((viewport - RowHeight) / Stride), 0, MaximumPinnedParents);

    public static double StickyTranslation(int index, int slot, double scroll) =>
        scroll + (double)(slot - index) * Stride;

    public static double CollapseAnchor(int index, double viewport) =>
        (double)Math.Max(0, index - PinnedCapacity(viewport)) * Stride;

    /// <summary>Builds parent indexes for a flattened pre-order tree.</summary>
    public static int[] ParentIndexes(IReadOnlyList<int> depths)
    {
        var parents = new int[depths.Count];
        var ancestors = new Stack<int>();
        for (int i = 0; i < depths.Count; i++)
        {
            while (ancestors.Count > 0 && depths[ancestors.Peek()] >= depths[i])
                ancestors.Pop();
            parents[i] = ancestors.TryPeek(out int parent) ? parent : -1;
            ancestors.Push(i);
        }
        return parents;
    }

    /// <summary>Returns the nearest three ancestors, from outermost to innermost.</summary>
    public static int PinnedParents(int[] parents, double offset, Span<int> destination)
    {
        if (parents.Length == 0 || !double.IsFinite(offset) || offset < Stride) return 0;
        int row = (int)Math.Clamp(Math.Floor(offset / Stride), 0, parents.Length - 1d);
        int count = 0;
        for (int parent = parents[row]; parent >= 0 && count < Math.Min(destination.Length, MaximumPinnedParents);
            parent = parents[parent])
            destination[count++] = parent;
        destination[..count].Reverse();
        return count;
    }

    public static double PaddingToKeepOffset(int count, double scroll, double viewport) =>
        scroll > 0 ? Math.Ceiling(Math.Max(0, scroll + viewport - RowsHeight(count))) : 0;
}
