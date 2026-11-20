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
        UpdateSystemPlayback(current, force: true, overridePlaying: true);
        try { _systemPlayback.EnsureRunning(); }
        catch (Exception ex) { _view.Status.Text = $"Системный плеер недоступен: {ex.Message}"; }
        _positions.Start();
        if (_windowVisible) _view.StartPlaybackVisuals(() => _playback.Spectrum);
        _view.Status.Text = "";
        _worker = _playback.RunAsync(new PlatformAudioOutput(), _settings.FadeInMilliseconds, stop.Token);
        UpdateQueueAnalysis();
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
                UpdateQueueAnalysis();
                UpdateTransport();
                ProcessSystemTrackRequests();
            }
        }
    }

    /// <summary>Suspends player visuals and FFT while keeping playback and queue advancement active.</summary>
    internal void SetWindowVisible(bool visible)
    {
        if (_closed || _windowVisible == visible) return;

        var snapshot = _playback.Snapshot;
        // Reconcile the queue before redrawing, so a track change in the background never replays its animation.
        if (visible) UpdatePositions(snapshot);
        _windowVisible = visible;
        UpdateQueueAnalysis();
        UpdatePositionRefreshInterval();
        UpdateSpectrumAnalysis();
        if (!visible)
        {
            _view.ResetAnimations();
            return;
        }

        if (IsPlayerVisible) RestorePlayer(snapshot);
        if (_worker is not null) _view.StartPlaybackVisuals(() => _playback.Spectrum);
        ProcessSystemTrackRequests();
    }

    private void UpdatePositionRefreshInterval() =>
        _positions.Interval = IsPlayerVisible ? VisibleRefreshInterval : HiddenRefreshInterval;

    private void RestorePlayer(QueueSnapshot snapshot)
    {
        bool transitioning = IsTransitioning(snapshot);
        _view.ResumeAnimations(snapshot, !IsStopping && !transitioning, showOverview: !transitioning,
            animatePlayheads: _worker is not null);
        UpdateQueue();
    }

    private void UpdateSpectrumAnalysis() =>
        _playback.SetSpectrumEnabled(_windowVisible && _settings.ShowSpectrum && _settings.SpectrumWidthPercent > 0);

    internal void StopPlayback()
    {
        if (_stop is { IsCancellationRequested: false } stop)
        {
            stop.Cancel();
            _view.StopPlaybackVisuals();
            if (!_closed) UpdateTransport();
        }
    }

    private void UpdatePositions() => UpdatePositions(_playback.Snapshot);

    /// <summary>Advances queue and media state, updating the player view only while it is visible.</summary>
    private void UpdatePositions(QueueSnapshot snapshot)
    {
        UpdateDecks(snapshot);
        UpdateSystemPlayback(snapshot.Current);
        if (snapshot.Current is not { } current)
        {
            return;
        }

        if (!ReferenceEquals(current.Track, _shownCurrent))
        {
            bool animate = IsPlayerVisible && ReferenceEquals(_shownIncoming, current.Track);
            _shownIncoming = null;
            _shownCurrent = current.Track;
            SetSelection(null, null);
            if (_library.Find(current.Track) is { } file)
            {
                _queue.SetCurrent(file);
            }
            if (animate)
            {
                UpdateQueueAnalysis();
                _coverAnimation = FinishCoverAsync(current.Track);
            }
            else
            {
                if (IsPlayerVisible) _view.ShowCurrent(current.Track);
                _ = SelectDefaultAsync();
            }
            UpdateTransport();
        }

        if (snapshot.Incoming is { } incoming && !ReferenceEquals(_shownIncoming, incoming.Track))
        {
            _shownIncoming = incoming.Track;
            if (IsPlayerVisible) _ = _view.BeginMixAsync(incoming.Track, _library.Find(incoming.Track));
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
            ProcessSystemTrackRequests();
        }
    }

    /// <summary>Keeps scrubbing locked from the mix request through the cover handoff.</summary>
    private bool IsTransitioning(QueueSnapshot snapshot) =>
        snapshot.IsMixing || _shownIncoming is not null || _view.IsHandingOff;

    private void UpdateDecks(QueueSnapshot snapshot)
    {
        if (!IsPlayerVisible) return;
        bool transitioning = IsTransitioning(snapshot);
        _view.ShowDecks(snapshot, !_closed && !IsStopping && !transitioning, showOverview: !transitioning);
    }

    private void ShowCurrent(PlayingTrack track)
    {
        _shownCurrent = track.Track;
        if (!IsPlayerVisible) return;
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
