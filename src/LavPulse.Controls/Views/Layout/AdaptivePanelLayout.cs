namespace Laveyana.LavPulse.Controls;

/// <summary>Shared width policy for panels with navigation and action rails.</summary>
internal readonly record struct AdaptivePanelLayout(double Width, double SideWidth)
{
    public const double Padding = 16;
    public const double Gap = 12;
    public const double CompactWidth = 660;
    public const double MinimumSide = 180;
    public const double MaximumSide = 240;
    public const double MinimumContent = 420;
    public const double MinimumWideWidth = 2 * Padding + 2 * Gap + 2 * MinimumSide + MinimumContent;
    public const double MaximumWidth = CompactWidth + 2 * Gap + 2 * MaximumSide;
    public const double PhoneMinimumSide = 100;
    public const double PhoneMinimumContent = 320;
    public const double PhoneMinimumWideWidth = 2 * Padding + 2 * Gap + 2 * PhoneMinimumSide + PhoneMinimumContent;

    public bool IsWide => SideWidth > 0;
    public bool HasCompactRails => IsWide && SideWidth < MinimumSide;
    public double ContentWidth => Math.Max(0, Width - 2 * Padding -
        (IsWide ? 2 * (SideWidth + Gap) : 0));

    public static AdaptivePanelLayout Measure(double width, double height, bool phone = false)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return default;
        double minimum = phone ? PhoneMinimumWideWidth : MinimumWideWidth;
        if (width <= height || width < minimum)
            return new(Math.Min(width, CompactWidth), 0);

        double total = Math.Min(width, MaximumWidth);
        if (phone && total < MinimumWideWidth)
        {
            // Smoothly grow mobile rails into the desktop layout at 836 DIP.
            double progress = (total - PhoneMinimumWideWidth) / (MinimumWideWidth - PhoneMinimumWideWidth);
            return new(total, PhoneMinimumSide + (MinimumSide - PhoneMinimumSide) * progress);
        }
        // Keep the center aligned with its compact position and give both
        // rails equal widths, even when a panel only needs one of them.
        double side = Math.Clamp((total - CompactWidth - 2 * Gap) / 2, MinimumSide, MaximumSide);
        return new(total, side);
    }
}
