using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;
using Microsoft.Maui.ApplicationModel;
#if IOS
using IosPage = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page;
using IosStatusBarHiddenMode = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.StatusBarHiddenMode;
#endif

namespace Laveyana.Audio.Maui;

/// <summary>Coordinates the panels and applies player preferences.</summary>
public sealed partial class MainPage
{
    private PlayerSettings? _appearanceDraft;
    private bool _applyingAppearance;

    private void ApplyEqualizer(EqualizerSettings settings)
    {
        _equalizer = settings;
        _playback.SetEqualizer(settings);
        _view.SetEqualizerEnabled(settings.Enabled);
    }

    private void ToggleEqualizer(bool enabled)
    {
        ApplyEqualizer(_equalizer.WithEnabled(enabled));
        PlayerPreferences.SaveEqualizer(_equalizer);
    }

    private void OpenEqualizer() =>
        OpenPanel(() => new EqualizerPanel(_equalizer, ApplyEqualizer, ClosePanel));

    private void SelectRecommendations(bool enabled)
    {
        _recommendations = enabled;
        _playback.SetQueueEnabled(!enabled);
        UpdateTransport();
    }

    private void OpenSettings() =>
        OpenPanel(() => new PlayerSettingsPanel(_settings, value => ApplySettings(value), PickFilesAsync, PickFolderAsync,
            ClosePanel, PreviewAppearance));

    private void OpenPanel(Func<View> createPanel)
    {
        if (_closed || _view.Panel is not null) return;
        try
        {
            var panel = createPanel();
            _view.ShowPanel(panel);
            _view.SuspendPlayerAnimations();
            UpdatePositionRefreshInterval();
        }
        catch (Exception ex)
        {
            ClosePanel();
            _view.Status.Text = $"Не удалось открыть панель. {ex.Message}";
        }
    }

    private void ClosePanel()
    {
        if (_view.Panel is null) return;
        SaveOpenEqualizer();

        var snapshot = _playback.Snapshot;
        UpdatePositions(snapshot);
        _view.ShowPanel(null);
        if (_appearanceDraft is not null)
        {
            _appearanceDraft = null;
            RefreshAppearance();
        }
        UpdatePositionRefreshInterval();
        if (IsPlayerVisible) RestorePlayer(snapshot);
    }

    private void SaveOpenEqualizer()
    {
        if (_view.Panel is EqualizerPanel equalizer)
        {
            equalizer.CommitPendingEdits();
            PlayerPreferences.SaveEqualizer(_equalizer);
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_view.Panel is null) return base.OnBackButtonPressed();
        if (_view.Panel is PlayerSettingsPanel settings && settings.TryClosePaletteEditor()) return true;
        ClosePanel();
        return true;
    }

    private void PreviewAppearance(PlayerSettings draft)
    {
        _appearanceDraft = _settings with
        {
            Theme = draft.Theme, DarkBackground = draft.DarkBackground, HideStatusBar = draft.HideStatusBar,
            Palette = draft.Palette, CustomColors = draft.CustomColors, SpectrumColors = draft.SpectrumColors
        };
        RefreshAppearance();
    }

    internal void RefreshAppearance()
    {
        if (_applyingAppearance || _closed) return;
        _applyingAppearance = true;
        try
        {
            var settings = _appearanceDraft ?? _settings;
#if !WINDOWS
            var app = Application.Current;
            if (app is not null)
                app.UserAppTheme = settings.Theme switch
                {
                    PlayerTheme.Light => AppTheme.Light,
                    PlayerTheme.Dark => AppTheme.Dark,
                    _ => AppTheme.Unspecified
                };
#endif
            _view.ApplyAppearance(settings);
            BackgroundColor = _view.BackgroundColor;
#if ANDROID
            if (Window?.Handler?.PlatformView is MainActivity activity)
                activity.ApplySystemBarAppearance(_view.IsLightAppearance, settings.HideStatusBar);
#elif IOS
            IosPage.SetPrefersStatusBarHidden(this, settings.HideStatusBar ? IosStatusBarHiddenMode.True : IosStatusBarHiddenMode.False);
#endif
        }
        finally { _applyingAppearance = false; }
    }

    private void ApplySettings(PlayerSettings value, bool save = true)
    {
        _appearanceDraft = null;
        _settings = value;
        _playback.SetAutoMix(value.AutoMix);
        _playback.SetCrossfadeDuration(value.CrossfadeMilliseconds);
        UpdateSpectrumAnalysis();
        RefreshAppearance();
        if (save)
        {
            PlayerPreferences.Save(value);
#if ANDROID
            if (Window?.Handler?.PlatformView is MainActivity activity)
                activity.SetPlayerOnLockScreen(value.ShowOnLockScreen);
#endif
        }
        UpdateQueue();
    }
}
