using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Settings;

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
        return new PlayerSettings
        {
            FadeInMilliseconds = Math.Clamp(Preferences.Default.Get("fade-in-ms", defaults.FadeInMilliseconds), 0, 3000),
            FadeOutMilliseconds = Math.Clamp(Preferences.Default.Get("fade-out-ms", defaults.FadeOutMilliseconds), 0, 3000),
            CrossfadeMilliseconds = Math.Clamp(Preferences.Default.Get("crossfade-ms", defaults.CrossfadeMilliseconds), 0, 12000),
            AutoMix = Preferences.Default.Get("automix", defaults.AutoMix),
            WaveformOverview = Preferences.Default.Get("waveform-overview", defaults.WaveformOverview),
            WaveformDetailSeconds = Math.Clamp(Preferences.Default.Get("waveform-detail-seconds", defaults.WaveformDetailSeconds),
                PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds),
            Palette = (PlayerPalette)Math.Clamp(Preferences.Default.Get("palette", (int)defaults.Palette), 0, (int)PlayerPalette.Warm),
            SpectrumColors = (SpectrumColorMode)Math.Clamp(Preferences.Default.Get("spectrum-colors", (int)defaults.SpectrumColors),
                0, (int)SpectrumColorMode.Monochrome),
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
        Preferences.Default.Set("waveform-overview", settings.WaveformOverview);
        Preferences.Default.Set("waveform-detail-seconds", settings.WaveformDetailSeconds);
        Preferences.Default.Set("palette", (int)settings.Palette);
        Preferences.Default.Set("spectrum-colors", (int)settings.SpectrumColors);
        Preferences.Default.Set("spectrum-level-brightness", settings.SpectrumLevelBrightness);
        Preferences.Default.Set("spectrum-minimum-brightness", settings.SpectrumMinimumBrightnessPercent);
        Preferences.Default.Set("spectrum-full-brightness", settings.SpectrumFullBrightnessPercent);
        Preferences.Default.Set("spectrum-top-padding", settings.SpectrumTopPaddingRows);
        Preferences.Default.Set("spectrum-bottom-padding", settings.SpectrumBottomPaddingRows);
        Preferences.Default.Set("spectrum-width", settings.SpectrumWidthPercent);
        Preferences.Default.Set("show-spectrum", settings.ShowSpectrum);
        Preferences.Default.Set("show-peaks", settings.ShowPeaks);
    }
}
