namespace Laveyana.LavPulse.Controls;

internal enum ListTouchIntent { Pending, Horizontal, Vertical }

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

    // Classify once at touch slop; vertical movement belongs to scrolling.
    public static ListTouchIntent ClassifyTouch(double x, double y, double threshold)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return ListTouchIntent.Vertical;
        double horizontal = Math.Abs(x), vertical = Math.Abs(y);
        if (horizontal >= Math.Max(threshold, vertical * 1.3)) return ListTouchIntent.Horizontal;
        if (vertical >= threshold) return ListTouchIntent.Vertical;
        return ListTouchIntent.Pending;
    }

    public const int MaximumPinnedParents = 3;

    // Keep room for at least one ordinary row, including in short landscape windows.
    public static int PinnedCapacity(double viewport) =>
        !double.IsFinite(viewport) || viewport < RowHeight ? 0 :
            (int)Math.Clamp(Math.Floor((viewport - RowHeight) / Stride), 0, MaximumPinnedParents);

    // Cover the whole slot, including the gap after the final pinned row.
    public static double PinnedAreaHeight(int count) =>
        (double)Math.Clamp(count, 0, MaximumPinnedParents) * Stride;

    // Prefer below/right of the press; flip when there is room on the other
    // side and clamp for narrow windows or menus larger than the free space.
    public static (double X, double Y) ContextMenuPosition(double x, double y,
        double width, double height, double menuWidth, double menuHeight)
    {
        const double edge = 8, gap = 4;
        static double Axis(double anchor, double extent, double size)
        {
            double start = anchor + gap;
            if (start + size > extent - edge && anchor - gap - size >= edge)
                start = anchor - gap - size;
            return Math.Clamp(start, edge, Math.Max(edge, extent - edge - size));
        }
        return (Axis(x, width, menuWidth), Axis(y, height, menuHeight));
    }

    public static (double X, double Y) DropdownPosition(double x, double top, double triggerHeight,
        double width, double height, double menuWidth, double menuHeight)
    {
        const double edge = 8, gap = 4;
        double y = top + triggerHeight + gap;
        if (y + menuHeight > height - edge && top - gap - menuHeight >= edge)
            y = top - gap - menuHeight;
        return (Math.Clamp(x, edge, Math.Max(edge, width - edge - menuWidth)),
            Math.Clamp(y, edge, Math.Max(edge, height - edge - menuHeight)));
    }

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
