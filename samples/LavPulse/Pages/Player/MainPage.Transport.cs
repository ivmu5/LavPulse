using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    /// <summary>Requests the pending mix and starts the output if the player was paused.</summary>
    private async Task StartMixAsync(bool waitForPlayback = true)
    {
        if (_closed || _recommendations || IsTransitioning(_playback.Snapshot) || IsStopping ||
            _selected is not { } file || !await ArmAsync(file) || _closed || _recommendations || IsStopping ||
            !ReferenceEquals(_selected, file) || IsTransitioning(_playback.Snapshot) || !_playback.RequestMix())
        {
            return;
        }
        UpdatePositions();
        UpdateTransport();
        if (_worker is null)
        {
            var playback = PlayAsync(restartAtEnd: false);
            if (waitForPlayback) await playback;
        }
    }

    /// <summary>Short press always requests the previous track, regardless of current playback position.</summary>
    private async Task PreviousAsync()
    {
        var snapshot = _playback.Snapshot;
        if (_closed || IsStopping || IsTransitioning(snapshot) || snapshot.Current is not { } current)
        {
            return;
        }
        if (!_recommendations && _queue.Previous is { } previous)
            await ReplayHistoryAsync(previous);
    }

    /// <summary>Long press restarts the currently playing deck without changing the queue.</summary>
    private void RestartCurrent()
    {
        if (_closed || IsStopping || IsTransitioning(_playback.Snapshot)) return;
        if (_playback.Snapshot.Current is { } current) Seek(current.Track, 0);
    }

    /// <summary>Commits a seek preview to a still-active deck.</summary>
    private void Seek(CachedTrack track, long frame)
    {
        if (!_closed && !IsStopping && !IsTransitioning(_playback.Snapshot) && _playback.Seek(track, frame))
        {
            _view.SnapPlayheadsOnNextPosition();
            UpdatePositions();
        }
    }

    private async Task TogglePlaybackAsync()
    {
        if (_worker is not null)
        {
            if (!IsStopping && _playback.RequestPause(_settings.FadeOutMilliseconds))
            {
                _pausing = true;
                _view.CurrentDeck.CancelScrub();
                UpdateTransport();
            }
            return;
        }

        await PlayAsync();
    }
}
