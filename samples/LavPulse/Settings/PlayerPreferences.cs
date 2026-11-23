using Laveyana.LavPulse.Controls;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Settings;

/// <summary>Stores player settings in the platform's local preferences.</summary>
internal static class PlayerPreferences
{
    /// <summary>Loads the saved master EQ without changing the default bypass for new installs.</summary>
    public static EqualizerSettings LoadEqualizer()
    {
        var settings = EqualizerSettings.Flat.WithEnabled(Preferences.Default.Get("equalizer-enabled", false))
            .WithOutputGain(Preferences.Default.Get("equalizer-output-gain", 0d));
        for (int band = 0; band < EqualizerSettings.Frequencies.Count; band++)
        {
            settings = settings.WithGain(band, Preferences.Default.Get($"equalizer-gain-{band}", 0d));
        }
        return settings;
    }

    public static void SaveEqualizer(EqualizerSettings settings)
    {
        Preferences.Default.Set("equalizer-enabled", settings.Enabled);
        Preferences.Default.Set("equalizer-output-gain", settings.OutputGainDb);
        for (int band = 0; band < EqualizerSettings.Frequencies.Count; band++)
        {
            Preferences.Default.Set($"equalizer-gain-{band}", settings.GainAt(band));
        }
    }

    public static PlayerSettings Load()
    {
        var defaults = new PlayerSettings();
        var spectrumFrameRate = (SpectrumFrameRate)Preferences.Default.Get(
            "spectrum-frame-rate", (int)defaults.SpectrumFrameRate);
        return new PlayerSettings
        {
            FadeInMilliseconds = Math.Clamp(Preferences.Default.Get("fade-in-ms", defaults.FadeInMilliseconds), 0, 3000),
            FadeOutMilliseconds = Math.Clamp(Preferences.Default.Get("fade-out-ms", defaults.FadeOutMilliseconds), 0, 3000),
            CrossfadeMilliseconds = Math.Clamp(Preferences.Default.Get("crossfade-ms", defaults.CrossfadeMilliseconds), 0, 12000),
            AutoMix = Preferences.Default.Get("automix", defaults.AutoMix),
            StorageDirectory = Preferences.Default.Get("library-storage-requested", defaults.StorageDirectory),
            AnalysisParallelism = Math.Clamp(Preferences.Default.Get("analysis-parallelism", defaults.AnalysisParallelism),
                PlayerSettings.DefaultAnalysisParallelism, PlayerSettings.MaximumAnalysisParallelism),
            ShowOnLockScreen = Preferences.Default.Get("show-player-on-lock-screen", defaults.ShowOnLockScreen),
            HideStatusBar = Preferences.Default.Get("hide-status-bar", defaults.HideStatusBar),
            WaveformOverview = Preferences.Default.Get("waveform-overview", defaults.WaveformOverview),
            WaveformOverviewBeatGrid = Preferences.Default.Get("waveform-overview-beat-grid", defaults.WaveformOverviewBeatGrid),
            DetailedWaveform = (DeckWaveformMode)Math.Clamp(
                Preferences.Default.Get("detailed-waveform-mode", (int)defaults.DetailedWaveform),
                0, (int)DeckWaveformMode.Transients),
            WaveformDetailSeconds = Math.Clamp(Preferences.Default.Get("waveform-detail-seconds", defaults.WaveformDetailSeconds),
                PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds),
            Theme = (PlayerTheme)Math.Clamp(Preferences.Default.Get("interface-theme", (int)defaults.Theme), 0, (int)PlayerTheme.Light),
            DarkBackground = (DarkBackground)Math.Clamp(Preferences.Default.Get("dark-background", (int)defaults.DarkBackground),
                0, (int)DarkBackground.Amoled),
            Palette = (PlayerPalette)Math.Clamp(Preferences.Default.Get("palette", (int)defaults.Palette), 0, (int)PlayerPalette.Custom),
            CustomColors = new PlayerColors(
                LoadPaletteColor("custom-palette-accent", defaults.CustomColors.Accent),
                LoadPaletteColor("custom-palette-alternate", defaults.CustomColors.Alternate),
                LoadPaletteColor("custom-palette-low", defaults.CustomColors.WaveLow),
                LoadPaletteColor("custom-palette-mid", defaults.CustomColors.WaveMid)),
            SpectrumColors = (SpectrumColorMode)Math.Clamp(Preferences.Default.Get("spectrum-colors", (int)defaults.SpectrumColors),
                0, (int)SpectrumColorMode.Monochrome),
            SpectrumFrameRate = Enum.IsDefined(spectrumFrameRate) ? spectrumFrameRate : defaults.SpectrumFrameRate,
            SpectrumLevelBrightness = Preferences.Default.Get("spectrum-level-brightness", defaults.SpectrumLevelBrightness),
            SpectrumMinimumBrightnessPercent = Math.Clamp(
                Preferences.Default.Get("spectrum-minimum-brightness", defaults.SpectrumMinimumBrightnessPercent), 0, 100),
            SpectrumFullBrightnessPercent = Math.Clamp(
                Preferences.Default.Get("spectrum-full-brightness", defaults.SpectrumFullBrightnessPercent), 1, 100),
            SpectrumTopPaddingRows = Math.Clamp(
                Preferences.Default.Get("spectrum-top-padding", defaults.SpectrumTopPaddingRows), 0, PlayerSettings.MaximumSpectrumPaddingRows),
            SpectrumBottomPaddingRows = Math.Clamp(
                Preferences.Default.Get("spectrum-bottom-padding", defaults.SpectrumBottomPaddingRows), 0, PlayerSettings.MaximumSpectrumPaddingRows),
            SpectrumWidthPercent = Math.Clamp(
                Preferences.Default.Get("spectrum-width", defaults.SpectrumWidthPercent), 0, PlayerSettings.MaximumSpectrumWidthPercent),
            ShowSpectrum = Preferences.Default.Get("show-spectrum", defaults.ShowSpectrum),
            ShowPeaks = Preferences.Default.Get("show-peaks", defaults.ShowPeaks)
        };
    }

    public static void Save(PlayerSettings settings)
    {
        Preferences.Default.Set("fade-in-ms", settings.FadeInMilliseconds);
        Preferences.Default.Set("fade-out-ms", settings.FadeOutMilliseconds);
        Preferences.Default.Set("crossfade-ms", settings.CrossfadeMilliseconds);
        Preferences.Default.Set("automix", settings.AutoMix);
        Preferences.Default.Set("library-storage-requested", settings.StorageDirectory);
        Preferences.Default.Set("analysis-parallelism", settings.AnalysisParallelism);
        Preferences.Default.Set("show-player-on-lock-screen", settings.ShowOnLockScreen);
        Preferences.Default.Set("hide-status-bar", settings.HideStatusBar);
        Preferences.Default.Set("waveform-overview", settings.WaveformOverview);
        Preferences.Default.Set("waveform-overview-beat-grid", settings.WaveformOverviewBeatGrid);
        Preferences.Default.Set("detailed-waveform-mode", (int)settings.DetailedWaveform);
        Preferences.Default.Set("waveform-detail-seconds", settings.WaveformDetailSeconds);
        Preferences.Default.Set("interface-theme", (int)settings.Theme);
        Preferences.Default.Set("dark-background", (int)settings.DarkBackground);
        Preferences.Default.Set("palette", (int)settings.Palette);
        Preferences.Default.Set("custom-palette-accent", settings.CustomColors.Accent.ToInt());
        Preferences.Default.Set("custom-palette-alternate", settings.CustomColors.Alternate.ToInt());
        Preferences.Default.Set("custom-palette-low", settings.CustomColors.WaveLow.ToInt());
        Preferences.Default.Set("custom-palette-mid", settings.CustomColors.WaveMid.ToInt());
        Preferences.Default.Set("spectrum-colors", (int)settings.SpectrumColors);
        Preferences.Default.Set("spectrum-frame-rate", (int)settings.SpectrumFrameRate);
        Preferences.Default.Set("spectrum-level-brightness", settings.SpectrumLevelBrightness);
        Preferences.Default.Set("spectrum-minimum-brightness", settings.SpectrumMinimumBrightnessPercent);
        Preferences.Default.Set("spectrum-full-brightness", settings.SpectrumFullBrightnessPercent);
        Preferences.Default.Set("spectrum-top-padding", settings.SpectrumTopPaddingRows);
        Preferences.Default.Set("spectrum-bottom-padding", settings.SpectrumBottomPaddingRows);
        Preferences.Default.Set("spectrum-width", settings.SpectrumWidthPercent);
        Preferences.Default.Set("show-spectrum", settings.ShowSpectrum);
        Preferences.Default.Set("show-peaks", settings.ShowPeaks);
    }

    private static Color LoadPaletteColor(string key, Color fallback)
    {
        var color = Color.FromInt(Preferences.Default.Get(key, fallback.ToInt()));
        return color.Alpha == 1 ? color : fallback;
    }
}
