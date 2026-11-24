using Laveyana.LavPulse.Controls;
using Laveyana.LavPulse.Settings;
using Laveyana.Audio.Playback;
using Microsoft.Maui.ApplicationModel;
using System.Reflection;
#if IOS
using IosPage = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page;
using IosStatusBarHiddenMode = Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.StatusBarHiddenMode;
#endif

namespace Laveyana.LavPulse;

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

    private bool OpenEqualizer() =>
        OpenPanel(() => new EqualizerPanel(_equalizer, ApplyEqualizer, ClosePanel)) is not null;

    private void SelectRecommendations(bool enabled)
    {
        _recommendations = enabled;
        _playback.SetQueueEnabled(!enabled);
        UpdateTransport();
    }

    private bool OpenSettings()
    {
        var settings = OpenPanel(() =>
            new PlayerSettingsPanel(_settings, value => ApplySettings(value), ClosePanel, PreviewAppearance,
                GetStorageOptions(), PickStorageFolderAsync,
                CopyReportTextAsync, ExportReportZipAsync, ReportSummaryAsync, ClearReportsAsync,
                typeof(MainPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));
        if (settings is null) return false;
        settings.PaletteEditingChanged += editing =>
        {
            if (editing) RecordOpeningScreen();
            else _forwardNavigation.Push(() => _view.Panel is PlayerSettingsPanel panel && panel.TryOpenPaletteEditor());
        };
        return true;
    }

    private TPanel? OpenPanel<TPanel>(Func<TPanel> createPanel) where TPanel : View
    {
        if (_closed || _view.Panel is not null) return null;
        try
        {
            var panel = createPanel();
            _view.ShowPanel(panel);
            _view.SuspendPlayerAnimations();
            _view.StopPlaybackVisuals();
            UpdateSpectrumAnalysis();
            UpdatePositionRefreshInterval();
            RecordOpeningScreen();
            return panel;
        }
        catch (Exception ex)
        {
            ClosePanel();
            _view.Status.Text = $"Не удалось открыть панель. {ex.Message}";
            return null;
        }
    }

    private void ClosePanel()
    {
        if (_view.Panel is not { } panel) return;
        SaveOpenEqualizer();
        if (panel is TrackLibraryPanel library) library.Cancel();

        var snapshot = _playback.PresentedSnapshot;
        UpdatePositions(snapshot);
        _view.ShowPanel(null);
        if (_appearanceDraft is not null)
        {
            _appearanceDraft = null;
            RefreshAppearance();
        }
        UpdatePositionRefreshInterval();
        UpdateSpectrumAnalysis();
        if (IsPlayerVisible)
        {
            RestorePlayer(snapshot);
            if (_worker is not null) _view.StartPlaybackVisuals(() => _playback.Spectrum);
        }
        RecordClosingPanel(panel);
    }

    private void SaveOpenEqualizer()
    {
        if (_view.Panel is EqualizerPanel equalizer)
        {
            equalizer.CommitPendingEdits();
            PlayerPreferences.SaveEqualizer(_equalizer);
        }
    }

    private void PreviewAppearance(PlayerSettings draft)
    {
        _appearanceDraft = _settings with
        {
            Theme = draft.Theme,
            DarkBackground = draft.DarkBackground,
            HideStatusBar = draft.HideStatusBar,
            Palette = draft.Palette,
            CustomColors = draft.CustomColors,
            SpectrumColors = draft.SpectrumColors,
            SpectrumFrameRate = draft.SpectrumFrameRate
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
        _playback.SetVisualDelay(value.VisualDelayMilliseconds);
        _library.SetAnalysisParallelism(value.AnalysisParallelism);
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
