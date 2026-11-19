using Microsoft.Maui.ApplicationModel;

namespace Laveyana.Audio.Maui.Controls;

internal enum MixerColor
{
    Background, Surface, Card, Overlay, Button, Selected, Border, Text, Muted, Accent, Primary, OnPrimary, Preview, Scrim, SwitchTrack
}

/// <summary>Derives UI surfaces and readable foregrounds from the selected player palette.</summary>
internal sealed class MixerTheme
{
    private static readonly Color DarkInk = Color.FromArgb("#0B2137");
    private static readonly Color LightInk = Color.FromArgb("#F4F6FF");
    private readonly Dictionary<MixerColor, Color> _colors;

    public static AppTheme SystemTheme =>
#if WINDOWS
        WindowsWindowChrome.SystemTheme;
#else
        Application.Current?.PlatformAppTheme ?? AppTheme.Dark;
#endif

    public static MixerTheme Default { get; } = Create(new PlayerSettings(), AppTheme.Dark);
    public bool IsLight { get; }
    public PlayerColors Artwork { get; }
    public PlayerColors Visualization { get; }
    public Color AlternateForeground { get; }
    public Color this[MixerColor role] => _colors[role];

    private MixerTheme(PlayerColors palette, bool light, bool amoled)
    {
        Artwork = palette;
        IsLight = light;
        Color tint = Blend(palette.WaveLow, palette.WaveMid, .5f);
        Color basis = light ? Color.FromArgb("#F7F9FC") : Color.FromArgb("#0B1020");
        Color background = amoled ? Colors.Black : Blend(basis, tint, light ? .04f : .025f);
        Color selected = Blend(background, palette.Accent, light ? .18f : .22f);
        Color surface = Blend(background, tint, .07f);
        Color card = Blend(background, tint, .12f);
        Color overlay = Blend(background, tint, .10f);
        Color button = Blend(background, tint, .16f);
        var surfaces = new[] { background, surface, card, button, overlay, selected };
        Color reference = light ? surfaces.MinBy(Luminance)! : surfaces.MaxBy(Luminance)!;
        Color toward = light ? Colors.Black : Colors.White;
        Color primaryInk = light ? LightInk : DarkInk;
        Color primary = EnsureContrast(EnsureContrast(palette.Accent, primaryInk, 4.5, toward), reference, 3, toward);
        Color accent = EnsureContrast(palette.Accent, reference, 4.5, toward);
        Color text = EnsureContrast(Blend(light ? DarkInk : LightInk, tint, .06f), reference, 7, toward);

        Color Graphic(Color color) => EnsureContrast(color, surface, 3, toward);
        Visualization = new(Graphic(palette.Accent), Graphic(palette.Alternate), Graphic(palette.WaveLow), Graphic(palette.WaveMid));
        AlternateForeground = EnsureContrast(palette.Alternate, reference, 4.5, toward);
        _colors = new()
        {
            [MixerColor.Background] = background,
            [MixerColor.Surface] = surface,
            [MixerColor.Card] = card,
            [MixerColor.Overlay] = overlay,
            [MixerColor.Button] = button,
            [MixerColor.Selected] = selected,
            [MixerColor.Border] = EnsureContrast(Blend(background, tint, .45f), reference, 3, toward),
            [MixerColor.Text] = text,
            [MixerColor.Muted] = EnsureContrast(Blend(text, reference, .32f), reference, 4.5, toward),
            [MixerColor.Accent] = accent,
            [MixerColor.Primary] = primary,
            [MixerColor.OnPrimary] = primaryInk,
            [MixerColor.Preview] = AlternateForeground,
            [MixerColor.Scrim] = Colors.Black.WithAlpha(light ? .42f : .76f),
            // Keep the checked track between the surrounding surfaces and the accent thumb.
            [MixerColor.SwitchTrack] = EnsureContrast(reference, reference, 2, accent)
        };
    }

    public static MixerTheme Create(PlayerSettings settings, AppTheme systemTheme)
    {
        bool light = settings.Theme == PlayerTheme.Light || settings.Theme == PlayerTheme.System && systemTheme == AppTheme.Light;
        return new MixerTheme(PlayerColors.For(settings), light, !light && settings.DarkBackground == DarkBackground.Amoled);
    }

    internal static Color Blend(Color first, Color second, float amount) => new(
        first.Red + (second.Red - first.Red) * amount,
        first.Green + (second.Green - first.Green) * amount,
        first.Blue + (second.Blue - first.Blue) * amount);

    private static Color EnsureContrast(Color color, Color background, double minimum, Color toward)
    {
        double backgroundLight = Luminance(background);
        double Ratio(Color ink)
        {
            double light = Luminance(ink);
            return (Math.Max(light, backgroundLight) + .05) / (Math.Min(light, backgroundLight) + .05);
        }

        if (Ratio(color) >= minimum) return color;
        if (Ratio(toward) < minimum) return toward;
        float low = 0;
        float high = 1;
        for (int i = 0; i < 12; i++)
        {
            float middle = (low + high) / 2;
            if (Ratio(Blend(color, toward, middle)) >= minimum) high = middle;
            else low = middle;
        }
        return Blend(color, toward, high);
    }

    private static double Luminance(Color color)
    {
        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return .2126 * Linear(color.Red) + .7152 * Linear(color.Green) + .0722 * Linear(color.Blue);
    }
}
