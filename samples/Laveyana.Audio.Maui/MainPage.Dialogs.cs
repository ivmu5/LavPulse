using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Coordinates the panels and applies player preferences.</summary>
public sealed partial class MainPage
{
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
        OpenPanel(() => new PlayerSettingsPanel(_settings, value => ApplySettings(value), PickFilesAsync, PickFolderAsync, ClosePanel));

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
        ClosePanel();
        return true;
    }

    private void ApplySettings(PlayerSettings value, bool save = true)
    {
        _settings = value;
        _playback.SetAutoMix(value.AutoMix);
        _playback.SetCrossfadeDuration(value.CrossfadeMilliseconds);
        UpdateSpectrumAnalysis();
        _view.ApplyAppearance(value);
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
