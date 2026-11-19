using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: dialogs workflow.</summary>
public sealed partial class MainPage
{
    private void ApplyEqualizer(EqualizerSettings settings)
    {
        _equalizer = settings;
        _playback.SetEqualizer(settings);
        _view.SetEqualizerEnabled(settings.Enabled);
    }

    private Task OpenEqualizerAsync() =>
        OpenModalAsync(dismiss => new EqualizerPage(_equalizer, ApplyEqualizer, PlayerPreferences.SaveEqualizer, dismiss));

    private void SelectRecommendations(bool enabled)
    {
        _recommendations = enabled;
        _playback.SetQueueEnabled(!enabled);
        UpdateTransport();
    }

    private Task OpenSettingsAsync() =>
        OpenModalAsync(dismiss => new PlayerSettingsPage(_settings, value => ApplySettings(value), PickFilesAsync, PickFolderAsync, dismiss));

    private async Task OpenModalAsync(Func<Func<Task>?, ContentPage> createPage)
    {
        if (_closed || _openingSettings || Navigation.ModalStack.Count > 0)
        {
            return;
        }

        _openingSettings = true;
        try
        {
#if WINDOWS
            // Switching native MAUI window roots can reactivate an AppWindow title bar that is disabled.
            if (_overlay is not null) return;
            // Cover the transport before constructing the settings controls. Image handlers otherwise
            // become visible at their unconstrained initial size while the new layout is measured.
            _overlay = new ContentView
            {
                BackgroundColor = _view.BackgroundColor,
                IsClippedToBounds = true
            };
            _view.IsVisible = false;
            _pageRoot.Children.Add(_overlay);
            await WaitForOverlayFrameAsync();
            if (_closed)
            {
                await CloseOverlayAsync();
                return;
            }

            var page = createPage(CloseOverlayAsync);
            var content = page.Content;
            page.Content = null;
            if (content is not null)
            {
                content.Opacity = 0;
                _overlay.Content = content;
                await WaitForOverlayFrameAsync();
                content.Opacity = 1;
            }
#else
            await Navigation.PushModalAsync(createPage(null));
#endif
        }
        catch (Exception ex)
        {
#if WINDOWS
            await CloseOverlayAsync();
#endif
            if (!_closed) _view.Status.Text = $"Не удалось открыть настройки. {ex.Message}";
        }
        finally
        {
            _openingSettings = false;
        }
    }

#if WINDOWS
    /// <summary>Allows the opaque overlay and its initial layout to reach the compositor.</summary>
    private Task WaitForOverlayFrameAsync()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(34), () => complete.TrySetResult());
        return complete.Task;
    }

    private Task CloseOverlayAsync()
    {
        if (_overlay is { } overlay)
        {
            _pageRoot.Children.Remove(overlay);
            _overlay = null;
            _view.IsVisible = true;
        }
        return Task.CompletedTask;
    }
#endif

    private void ApplySettings(PlayerSettings value, bool save = true)
    {
        _settings = value;
        _playback.SetAutoMix(value.AutoMix);
        _playback.SetCrossfadeDuration(value.CrossfadeMilliseconds);
        _playback.SetSpectrumEnabled(value.ShowSpectrum && value.SpectrumWidthPercent > 0);
        _view.ApplyAppearance(value);
        if (save)
        {
            PlayerPreferences.Save(value);
        }
        UpdateQueue();
    }
}
