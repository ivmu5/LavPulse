using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: rendering workflow.</summary>
public sealed partial class MainPage
{
    /// <summary>Keeps the cursor and pending transition when playback is paused and resumed.</summary>
    private async Task PlayAsync(bool restartAtEnd = true)
    {
        if (_closed || _worker is not null || _playback.Snapshot.Current is not { } current)
        {
            return;
        }

        if (restartAtEnd && current.Position >= current.Track.Waveform.TotalFrames)
        {
            _playback.Seek(current.Track, 0);
        }

        using var stop = new CancellationTokenSource();
        _stop = stop;
        // Start Android's foreground service before allowing the activity to go into background.
        UpdateSystemPlayback(force: true, overridePlaying: true);
        try { _systemPlayback.EnsureRunning(); }
        catch (Exception ex) { _view.Status.Text = $"Системный плеер недоступен: {ex.Message}"; }
        _positions.Start();
#if WINDOWS
        if (_windowVisible) _view.StartPlaybackVisuals(() => _playback.Spectrum);
#else
        _view.StartPlaybackVisuals(() => _playback.Spectrum);
#endif
        _view.Status.Text = "";
        _worker = _playback.RunAsync(new PlatformAudioOutput(), _settings.FadeInMilliseconds, stop.Token);
        UpdateTransport();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The window lifecycle can cancel output immediately.
        }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Ошибка воспроизведения. {ex.Message}";
        }
        finally
        {
            _worker = null;
            _stop = null;
            _pausing = false;
            _positions.Stop();
            _view.StopPlaybackVisuals();
            if (!_closed)
            {
                UpdatePositions();
                UpdateTransport();
            }
        }
    }

#if WINDOWS
    /// <summary>Reduces UI rendering when Windows hides the app; the mixer stays active.</summary>
    internal void SetWindowVisible(bool visible)
    {
        if (_closed || _windowVisible == visible) return;
        _windowVisible = visible;
        // Keep a slower UI snapshot pump for automatic transitions and queue advancement.
        _positions.Interval = TimeSpan.FromMilliseconds(visible ? 33 : 250);
        if (_worker is null) return;
        if (visible) _view.StartPlaybackVisuals(() => _playback.Spectrum);
        else _view.StopPlaybackVisuals();
    }
#endif

    internal void StopPlayback()
    {
        if (_stop is { IsCancellationRequested: false } stop)
        {
            stop.Cancel();
            _view.StopPlaybackVisuals();
            if (!_closed) UpdateTransport();
        }
    }

    /// <summary>Updates inexpensive position visuals and starts cover animations only on deck changes.</summary>
    private void UpdatePositions()
    {
        var snapshot = _playback.Snapshot;
        UpdateDecks(snapshot);
        UpdateSystemPlayback();
        if (snapshot.Current is not { } current)
        {
            return;
        }

        if (!ReferenceEquals(current.Track, _shownCurrent))
        {
            bool animate = ReferenceEquals(_shownIncoming, current.Track);
            _shownIncoming = null;
            _shownCurrent = current.Track;
            _selected = _armed = null;
            if (_library.Find(current.Track) is { } file)
            {
                _queue.SetCurrent(file);
            }
            if (animate)
            {
                _coverAnimation = FinishCoverAsync(current.Track);
            }
            else
            {
                _view.ShowCurrent(current.Track);
                _ = SelectDefaultAsync();
            }
            UpdateTransport();
        }

        if (snapshot.Incoming is { } incoming && !ReferenceEquals(_shownIncoming, incoming.Track))
        {
            _shownIncoming = incoming.Track;
            _ = _view.BeginMixAsync(incoming.Track, _library.Find(incoming.Track));
            UpdateTransport();
        }
    }

    /// <summary>Preloads the next track while the covers move between their final positions.</summary>
    private async Task FinishCoverAsync(CachedTrack track)
    {
        var animation = _view.FinishMixAsync(track, UpdateQueue);
        var preparation = _queue.Next is { } next && TrySetSelection(next) ? ArmAsync(next) : Task.CompletedTask;
        await animation;
        if (!_closed)
        {
            UpdateDecks(_playback.Snapshot);
        }
        await preparation;
        if (!_closed)
        {
            UpdateQueue();
        }
    }

    /// <summary>Keeps scrubbing locked from the mix request through the cover handoff.</summary>
    private bool IsTransitioning(QueueSnapshot snapshot) =>
        snapshot.IsMixing || _shownIncoming is not null || _view.IsHandingOff;

    private void UpdateDecks(QueueSnapshot snapshot)
    {
        bool transitioning = IsTransitioning(snapshot);
        _view.ShowDecks(snapshot, !_closed && !IsStopping && !transitioning, showOverview: !transitioning);
    }

    private void ShowCurrent(PlayingTrack track)
    {
        _shownCurrent = track.Track;
        _view.ShowCurrent(track.Track);
        _view.CurrentDeck.Show(track, true);
    }

    private void ToggleAutoMix() => ApplySettings(_settings with { AutoMix = !_settings.AutoMix });

    private void ToggleRepeatOne()
    {
        _repeatOne = !_repeatOne;
        _playback.SetRepeatOne(_repeatOne);
        UpdateTransport();
    }
}
