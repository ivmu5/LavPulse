namespace Laveyana.LavPulse.Controls;

/// <summary>Relative sRGB luminance and contrast against the existing transport assets.</summary>
internal static class IconContrast
{
    private static readonly double Dark = Luminance(11d/255, 33d/255, 55d/255);
    private static readonly double Light = Luminance(244d/255, 246d/255, 1);
    private static readonly double MutedDark = Luminance(38d/255, 52d/255, 79d/255);
    private static readonly double MutedLight = Luminance(179d/255, 190d/255, 224d/255);

    public static double Luminance(double red, double green, double blue)
    {
        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return .2126 * Linear(red) + .7152 * Linear(green) + .0722 * Linear(blue);
    }

    public static double Contrast(double a, double b) =>
        (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);

    public static bool DarkText(double background) =>
        Contrast(background, 0) >= Contrast(background, 1);

    public static bool DarkIcon(double background, string file)
    {
        bool muted = file is "player_delete.png" or "player_queue_add.png" or "player_edit.png" or "player_import.png";
        double dark = muted ? MutedDark : Dark;
        double light = muted || file == "player_reorder.png" ? MutedLight : Light;
        return Contrast(background, dark) >= Contrast(background, light);
    }

    // Smooth, reversible blend around the point where both variants have
    // equal contrast. Outside this short interval keep the stronger variant.
    public static double DarkIconWeight(double background, string file)
    {
        bool muted = file is "player_delete.png" or "player_queue_add.png" or "player_edit.png" or "player_import.png";
        double dark = muted ? MutedDark : Dark;
        double light = muted || file == "player_reorder.png" ? MutedLight : Light;
        return DarkWeight(background, dark, light);
    }

    public static double DarkTextWeight(double background) => DarkWeight(background, 0, 1);

    private static double DarkWeight(double background, double dark, double light)
    {
        // Equal contrast for a surface between the two foreground luminances.
        double center = Math.Sqrt((dark + .05) * (light + .05)) - .05;
        double t = Math.Clamp((background - center + .035) / .07, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static bool UseLightSuffix(string file, bool darkIcon)
    {
        // The play/pause/confirm assets were originally intended for primary
        // buttons and have the opposite variant naming from other icons.
        bool darkIsBase = file is "player_play.png" or "player_pause.png" or "player_confirm.png";
        return darkIcon != darkIsBase;
    }

    public static double BlendLuminance(double r, double g, double b, double tr, double tg, double tb, double opacity)
    {
        double a = Math.Clamp(opacity, 0, 1);
        return Luminance(r + (tr-r)*a, g + (tg-g)*a, b + (tb-b)*a);
    }
}
