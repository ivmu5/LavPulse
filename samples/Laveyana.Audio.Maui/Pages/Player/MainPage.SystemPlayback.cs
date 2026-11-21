using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

public sealed partial class MainPage
{
    /// <summary>Dispatches OS transport buttons to the same commands used by the player.</summary>
    private void OnSystemPlaybackCommand(SystemPlaybackCommand command) =>
        Dispatcher.Dispatch(() => _ = ExecuteSystemPlaybackCommandAsync(command));

    private async Task ExecuteSystemPlaybackCommandAsync(SystemPlaybackCommand command)
    {
        if (_closed) return;
        if (command == SystemPlaybackCommand.Interrupted)
        {
            StopPlayback();
            return;
        }
        if (IsLoadingFirst || IsStopping) return;
        try
        {
            switch (command)
            {
                case SystemPlaybackCommand.Play when _worker is null:
                    await PlayAsync();
                    break;
                case SystemPlaybackCommand.Pause when _worker is not null:
                    await TogglePlaybackAsync();
                    break;
                case SystemPlaybackCommand.Next:
                    if (_selected is null) await SelectDefaultAsync();
                    await StartMixAsync();
                    break;
                case SystemPlaybackCommand.Previous:
                    await PreviousAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Ошибка системного управления: {ex.Message}";
        }
    }

    /// <summary>Throttles position updates but immediately publishes track and transport changes.</summary>
    private void UpdateSystemPlayback(PlayingTrack? track, bool updateCommands = false, bool force = false,
        bool? overridePlaying = null)
    {
        if (_closed || track is not { } current) return;
        bool playing = overridePlaying ?? (_worker is not null && !_pausing && !(_stop?.IsCancellationRequested ?? false));
        bool playbackChanged = _lastSystemState is not { } last || !ReferenceEquals(last.Track, current.Track) ||
            last.Playing != playing || last.Speed != current.Speed ||
            (_worker is null && last.PositionFrames != current.Position);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
#if WINDOWS
        // Windows SMTC extrapolates the position itself; avoid expensive COM calls every frame.
        const int reportingIntervalMilliseconds = 5000;
#else
        const int reportingIntervalMilliseconds = 750;
#endif
        bool positionDue = playing &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_lastSystemUpdate, now).TotalMilliseconds >= reportingIntervalMilliseconds;
        bool publish = force || playbackChanged || positionDue;
        if (!publish && !updateCommands) return;

        bool canNext = !_recommendations && (_selected is not null || _queue.Next is not null);
        if (!publish && _lastSystemState?.CanNext == canNext) return;

        var state = new SystemPlaybackState(current.Track, current.Position, playing, canNext, true, current.Speed);
        _lastSystemState = state;
        _lastSystemUpdate = now;
        _systemPlayback.Publish(state);
    }
}
