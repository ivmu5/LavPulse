using Microsoft.Maui.ApplicationModel;
using Laveyana.Maui.UI.Themes;

namespace Laveyana.LavPulse.Controls;

internal enum MixerColor
{
    Background, Surface, Card, Overlay, Button, Selected, Border, Text, Muted, Accent, Primary, OnPrimary, Preview, Scrim, SwitchTrack
}

/// <summary>Derives UI surfaces and readable foregrounds from the selected player palette.</summary>
internal sealed class MixerTheme
{
    internal UiTheme SharedTheme { get; }
    private readonly bool _amoled;

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
    public Color this[MixerColor role] => role == MixerColor.Preview
        ? AlternateForeground : SharedTheme[SharedRole(role)];

    internal static UiColorRole SharedRole(MixerColor role) => role switch
    {
        MixerColor.Background => UiColorRole.Background,
        MixerColor.Surface => UiColorRole.Surface,
        MixerColor.Card => UiColorRole.Card,
        MixerColor.Overlay => UiColorRole.Overlay,
        MixerColor.Button => UiColorRole.Button,
        MixerColor.Selected => UiColorRole.Selected,
        MixerColor.Border => UiColorRole.Border,
        MixerColor.Text => UiColorRole.Text,
        MixerColor.Muted => UiColorRole.Muted,
        MixerColor.Accent => UiColorRole.Accent,
        MixerColor.Primary => UiColorRole.Primary,
        MixerColor.OnPrimary => UiColorRole.OnPrimary,
        MixerColor.Scrim => UiColorRole.Scrim,
        MixerColor.SwitchTrack => UiColorRole.SwitchTrack,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private MixerTheme(PlayerColors palette, bool light, bool amoled)
    {
        Artwork = palette;
        IsLight = light;
        _amoled = amoled;
        SharedTheme = UiTheme.Create(new UiThemeSettings
        {
            Mode = light ? UiThemeMode.Light : UiThemeMode.Dark,
            DarkBackground = amoled ? UiDarkBackground.Amoled : UiDarkBackground.Classic,
            Preset = UiPalettePreset.Custom,
            CustomPalette = new UiPalette(palette.Accent, palette.Alternate, palette.WaveLow, palette.WaveMid)
        });
        Color surface = SharedTheme[UiColorRole.Surface];
        Color[] surfaces = [SharedTheme[UiColorRole.Background], surface, SharedTheme[UiColorRole.Card],
            SharedTheme[UiColorRole.Button], SharedTheme[UiColorRole.Overlay], SharedTheme[UiColorRole.Selected]];
        Color reference = light ? surfaces.MinBy(Luminance)! : surfaces.MaxBy(Luminance)!;
        Color toward = light ? Colors.Black : Colors.White;
        // Only the audio visualization and secondary artwork color are player-specific.
        Color Graphic(Color color) => UiTheme.EnsureContrast(color, surface, 3, toward);
        Visualization = new(Graphic(palette.Accent), Graphic(palette.Alternate),
            Graphic(palette.WaveLow), Graphic(palette.WaveMid));
        AlternateForeground = UiTheme.EnsureContrast(palette.Alternate, reference, 4.5, toward);
    }

    public static MixerTheme Create(PlayerSettings settings, AppTheme systemTheme, MixerTheme? current = null)
    {
        bool light = settings.Theme == PlayerTheme.Light || settings.Theme == PlayerTheme.System && systemTheme == AppTheme.Light;
        bool amoled = !light && settings.DarkBackground == DarkBackground.Amoled;
        var palette = PlayerColors.For(settings);
        return current is not null && current.IsLight == light && current._amoled == amoled && current.Artwork == palette
            ? current : new MixerTheme(palette, light, amoled);
    }

    internal static Color Blend(Color first, Color second, float amount) =>
        UiTheme.Blend(first, second, amount);

    private static double Luminance(Color color)
    {
        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return .2126 * Linear(color.Red) + .7152 * Linear(color.Green) + .0722 * Linear(color.Blue);
    }
}
