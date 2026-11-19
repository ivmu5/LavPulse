namespace Laveyana.Audio.Maui.Controls;

public enum PlayerPalette
{
    LavPulse,
    Ocean,
    Warm,
    Custom
}

public enum SpectrumColorMode
{
    Split,
    FrequencyGradient,
    Monochrome
}

public enum PlayerTheme { System, Dark, Light }

public enum DarkBackground { Classic, Amoled }

/// <summary>Contains the user-facing preferences for playback and the player visuals.</summary>
public sealed record PlayerSettings
{
    public const int MinimumWaveformDetailSeconds = 5;
    public const int DefaultWaveformDetailSeconds = 15;
    public const int MaximumWaveformDetailSeconds = 60;
    public const int MaximumSpectrumPaddingRows = 28;
    public const int MaximumSpectrumWidthPercent = 45;

    public int FadeInMilliseconds { get; init; } = 300;
    public int FadeOutMilliseconds { get; init; } = 300;
    public int CrossfadeMilliseconds { get; init; } = 2500;
    public bool AutoMix { get; init; } = true;
    public bool ShowOnLockScreen { get; init; }
    public bool HideStatusBar { get; init; } = true;
    public bool WaveformOverview { get; init; }
    public int WaveformDetailSeconds { get; init; } = DefaultWaveformDetailSeconds;
    public PlayerPalette Palette { get; init; }
    public PlayerColors CustomColors { get; init; } = PlayerColors.For(PlayerPalette.LavPulse);
    public PlayerTheme Theme { get; init; } = PlayerTheme.Dark;
    public DarkBackground DarkBackground { get; init; }
    public SpectrumColorMode SpectrumColors { get; init; } = SpectrumColorMode.FrequencyGradient;
    public bool SpectrumLevelBrightness { get; init; } = true;
    public int SpectrumMinimumBrightnessPercent { get; init; } = 5;
    public int SpectrumFullBrightnessPercent { get; init; } = 70;
    public int SpectrumTopPaddingRows { get; init; }
    public int SpectrumBottomPaddingRows { get; init; }
    public int SpectrumWidthPercent { get; init; } = 20;
    public bool ShowSpectrum { get; init; } = true;
    public bool ShowPeaks { get; init; } = true;
}

public readonly record struct PlayerColors(Color Accent, Color Alternate, Color WaveLow, Color WaveMid)
{
    internal static PlayerColors For(PlayerSettings settings) =>
        settings.Palette == PlayerPalette.Custom ? settings.CustomColors : For(settings.Palette);

    internal static PlayerColors For(PlayerPalette palette) => palette switch
    {
        PlayerPalette.Ocean => new(Color.FromArgb("#8CEBFE"), Color.FromArgb("#A5B0FF"),
            Color.FromArgb("#589BEC"), Color.FromArgb("#8F84EA")),
        PlayerPalette.Warm => new(Color.FromArgb("#FFD39A"), Color.FromArgb("#FF9EAE"),
            Color.FromArgb("#E48275"), Color.FromArgb("#E88EBE")),
        _ => new(Color.FromArgb("#58E5FC"), Color.FromArgb("#FF9B65"),
            Color.FromArgb("#419DFB"), Color.FromArgb("#BF89E7"))
    };
}
