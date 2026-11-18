using Laveyana.Audio.Maui.Controls;

namespace Laveyana.Audio.Maui.Settings;

/// <summary>Stores player settings in the platform's local preferences.</summary>
internal static class PlayerPreferences
{
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
            Palette = (PlayerPalette)Math.Clamp(Preferences.Default.Get("palette", (int)defaults.Palette), 0, (int)PlayerPalette.Warm),
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
        Preferences.Default.Set("palette", (int)settings.Palette);
        Preferences.Default.Set("show-spectrum", settings.ShowSpectrum);
        Preferences.Default.Set("show-peaks", settings.ShowPeaks);
    }
}
