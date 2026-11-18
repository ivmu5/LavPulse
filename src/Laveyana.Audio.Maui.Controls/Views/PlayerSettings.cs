namespace Laveyana.Audio.Maui.Controls;

public enum PlayerPalette
{
    LavPulse,
    Ocean,
    Warm
}

public enum SpectrumColorMode
{
    Split,
    FrequencyGradient,
    Monochrome
}

/// <summary>Contains the user-facing preferences for playback and the player visuals.</summary>
public sealed record PlayerSettings
{
    public int FadeInMilliseconds { get; init; } = 300;
    public int FadeOutMilliseconds { get; init; } = 300;
    public int CrossfadeMilliseconds { get; init; } = 6000;
    public bool AutoMix { get; init; } = true;
    public bool WaveformOverview { get; init; }
    public PlayerPalette Palette { get; init; }
    public SpectrumColorMode SpectrumColors { get; init; }
    public bool ShowSpectrum { get; init; } = true;
    public bool ShowPeaks { get; init; } = true;
}

internal readonly record struct PlayerColors(Color Accent, Color Alternate, Color WaveLow, Color WaveMid)
{
    public static PlayerColors For(PlayerPalette palette) => palette switch
    {
        PlayerPalette.Ocean => new(Color.FromArgb("#8CEBFE"), Color.FromArgb("#A5B0FF"),
            Color.FromArgb("#589BEC"), Color.FromArgb("#8F84EA")),
        PlayerPalette.Warm => new(Color.FromArgb("#FFD39A"), Color.FromArgb("#FF9EAE"),
            Color.FromArgb("#E48275"), Color.FromArgb("#E88EBE")),
        _ => new(MixerStyle.Accent, MixerStyle.Alternate, MixerStyle.Blue, MixerStyle.Violet)
    };
}
