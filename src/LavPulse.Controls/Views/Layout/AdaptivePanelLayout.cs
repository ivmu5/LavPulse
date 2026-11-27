namespace Laveyana.LavPulse.Controls;

/// <summary>Shared width policy for compact, two-column and centered three-column panels.</summary>
internal readonly record struct AdaptivePanelLayout(double Width, double SideWidth, bool IsTwoColumn = false)
{
    public const double Padding = 16;
    public const double Gap = 12;
    public const double CompactWidth = 660;
    public const double MinimumSide = 180;
    public const double MaximumSide = 240;
    public const double MinimumContent = 420;
    public const double MinimumWideWidth = 2 * Padding + 2 * Gap + 2 * MinimumSide + MinimumContent;
    public const double MaximumWidth = CompactWidth + 2 * Gap + 2 * MaximumSide;
    public const double MinimumCompactSide = 116;
    public const double MinimumCompactContent = 320;
    public const double MinimumTwoColumnWidth = 2 * Padding + Gap + MinimumCompactSide + MinimumCompactContent;

    public bool IsWide => SideWidth > 0;
    public bool IsThreeColumn => IsWide && !IsTwoColumn;
    public bool HasCompactRails => IsWide && SideWidth < MinimumSide;
    public double ContentWidth => Math.Max(0, Width - 2 * Padding -
        (IsWide ? (IsTwoColumn ? 1 : 2) * (SideWidth + Gap) : 0));

    // A zero-width Grid column can still consume ColumnSpacing. In the
    // two-column layout put the sole gutter inside the occupied rail instead.
    public double ColumnSpacing => IsThreeColumn ? Gap : 0;
    public double RailGutter => IsTwoColumn ? Gap : 0;
    public double LeftColumnWidth(bool navigation) =>
        IsThreeColumn ? SideWidth : IsTwoColumn && navigation ? SideWidth + Gap : 0;
    public double RightColumnWidth(bool navigation) =>
        IsThreeColumn ? SideWidth : IsTwoColumn && !navigation ? SideWidth + Gap : 0;

    // Use actual available window dimensions rather than device idiom.
    public static bool IsShortLandscape(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && height > 0 &&
        width >= MinimumTwoColumnWidth && width > height && height < 520;

    public static bool UseSplitPlayer(double width, double height) =>
        IsShortLandscape(width, height) && width >= 640;

    public static bool UseSingleRowTransport(double width) => width >= 600;

    // Spread faders over a balanced number of rows, rather than leaving
    // only the output control in a third row on a five-column grid.
    public static int EqualizerColumns(double width, int count)
    {
        if (!double.IsFinite(width) || width <= 0 || count <= 0) return 1;
        int capacity = (int)Math.Clamp(Math.Floor((width + 4) / 64), 1, count);
        int rows = (count + capacity - 1) / capacity;
        return (count + rows - 1) / rows;
    }

    public static AdaptivePanelLayout Measure(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return default;
        if (width <= height || width < MinimumTwoColumnWidth)
            return new(Math.Min(width, CompactWidth), 0);

        double total = Math.Min(width, MaximumWidth);
        if (total < MinimumWideWidth)
        {
            // One real sidebar and a large content region; no empty opposite
            // rail on phones or narrow landscape desktop windows.
            double side = Math.Clamp((total - 2 * Padding - Gap) * .30,
                MinimumCompactSide, Math.Min(MaximumSide, total - 2 * Padding - Gap - MinimumCompactContent));
            return new(total, side, IsTwoColumn: true);
        }

        double rail = Math.Clamp((total - CompactWidth - 2 * Gap) / 2, MinimumSide, MaximumSide);
        return new(total, rail);
    }
}
