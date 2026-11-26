using Laveyana.Maui.UI.Themes;

namespace Laveyana.LavPulse.Controls;

/// <summary>One user-selectable writable storage volume or folder.</summary>
public readonly record struct LibraryStorageOption(string Directory, string Label);

public enum PlayerPalette
{
    // Preserve numeric preferences; append new palettes after Custom.
    LavPulse = 0,
    Ocean = 1,
    Warm = 2,
    Custom = 3,
    Mint = 4
}

/// <summary>Appearance of the detailed deck waveform; beat markers come from the saved rhythm analysis.</summary>
public enum DeckWaveformMode { Color, BeatGrid, Transients }

public enum SpectrumColorMode
{
    Split,
    FrequencyGradient,
    Monochrome
}

/// <summary>Limits how often the master spectrum redraws, with no app limit in automatic mode.</summary>
public enum SpectrumFrameRate
{
    Automatic = 0,
    Fps30 = 30,
    Fps60 = 60,
    Fps90 = 90,
    Fps120 = 120,
    Fps144 = 144,
    Fps165 = 165,
    Fps240 = 240,
    Fps360 = 360
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
    public const int MinimumAnalysisParallelism = TrackAnalysisParallelism.Minimum;
    public const int DefaultAnalysisParallelism = TrackAnalysisParallelism.Automatic;
    public const int MaximumAnalysisParallelism = TrackAnalysisParallelism.MaximumSupported;

    public static int ResolveAnalysisParallelism(int requested) =>
        TrackAnalysisParallelism.Resolve(requested, Environment.ProcessorCount);

    public int FadeInMilliseconds { get; init; } = 300;
    public int FadeOutMilliseconds { get; init; } = 300;
    public int CrossfadeMilliseconds { get; init; } = 2500;
    /// <summary>Extra UI presentation delay for Bluetooth and output-device buffering (audio is unaffected).</summary>
    public int VisualDelayMilliseconds { get; init; } = 0;
    public bool AutoMix { get; init; } = true;
    /// <summary>Zero selects a limit automatically based on the processors available to the app.</summary>
    public int AnalysisParallelism { get; init; } = DefaultAnalysisParallelism;
    public const int DefaultQueueAnalysisLookahead = 10;
    public const int MaximumQueueAnalysisLookahead = 100;
    public const int DefaultQueuePcmLookahead = 1;
    public const int MaximumQueuePcmLookahead = 20;
    public int QueueAnalysisLookahead { get; init; } = DefaultQueueAnalysisLookahead;
    public int QueuePcmLookahead { get; init; } = DefaultQueuePcmLookahead;
    public const int DefaultScanReturnSeconds = 10;
    public const int MaximumScanReturnSeconds = 60;
    /// <summary>Seconds of inactivity before returning to active scans; zero disables return after manual scrolling.</summary>
    public int ScanReturnSeconds { get; init; } = DefaultScanReturnSeconds;
    /// <summary>Empty = private app storage; otherwise a selected drive or folder containing LavPulse/library.</summary>
    public string StorageDirectory { get; init; } = "";
    public bool ShowOnLockScreen { get; init; }
    public bool HideStatusBar { get; init; } = true;
    public bool WaveformOverview { get; init; }
    public bool WaveformOverviewBeatGrid { get; init; }
    public DeckWaveformMode DetailedWaveform { get; init; } = DeckWaveformMode.Transients;
    public int WaveformDetailSeconds { get; init; } = DefaultWaveformDetailSeconds;
    public PlayerPalette Palette { get; init; }
    public PlayerColors CustomColors { get; init; } = PlayerColors.For(PlayerPalette.LavPulse);
    public PlayerTheme Theme { get; init; } = PlayerTheme.Dark;
    public DarkBackground DarkBackground { get; init; }
    public SpectrumColorMode SpectrumColors { get; init; } = SpectrumColorMode.FrequencyGradient;
    public SpectrumFrameRate SpectrumFrameRate { get; init; }
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
    private static PlayerColors FromShared(UiPalettePreset preset)
    {
        UiPalette colors = UiPaletteCatalog.Get(preset);
        return new(colors.Accent, colors.Alternate, colors.Low, colors.Mid);
    }

    private static readonly PlayerColors LavPulse = FromShared(UiPalettePreset.Laveyana);
    private static readonly PlayerColors Ocean = FromShared(UiPalettePreset.Ocean);
    private static readonly PlayerColors Warm = FromShared(UiPalettePreset.Warm);
    private static readonly PlayerColors Mint = FromShared(UiPalettePreset.Mint);

    internal static PlayerColors For(PlayerSettings settings) =>
        settings.Palette == PlayerPalette.Custom ? settings.CustomColors : For(settings.Palette);

    internal static PlayerColors For(PlayerPalette palette) => palette switch
    {
        PlayerPalette.Ocean => Ocean,
        PlayerPalette.Warm => Warm,
        PlayerPalette.Mint => Mint,
        _ => LavPulse
    };
}
