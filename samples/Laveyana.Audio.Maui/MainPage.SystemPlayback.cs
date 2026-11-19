using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: systemplayback workflow.</summary>
public sealed partial class MainPage
{
    /// <summary>Dispatches OS transport buttons to the same commands used by the player.</summary>
    private void OnSystemPlaybackCommand(SystemPlaybackCommand command) =>
        Dispatcher.Dispatch(() => _ = ExecuteSystemPlaybackCommandAsync(command));

    private async Task ExecuteSystemPlaybackCommandAsync(SystemPlaybackCommand command)
    {
        if (_closed || _loadingFirst || IsStopping) return;
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
    private void UpdateSystemPlayback(bool force = false, bool? overridePlaying = null)
    {
        if (_closed || _playback.Snapshot.Current is not { } current) return;
        bool playing = overridePlaying ?? (_worker is not null && !_pausing && !(_stop?.IsCancellationRequested ?? false));
        bool changed = !ReferenceEquals(_lastSystemTrack, current.Track) || playing != _lastSystemPlaying;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
#if WINDOWS
        // Windows SMTC extrapolates the position itself; avoid expensive COM calls every frame.
        const int reportingIntervalMilliseconds = 5000;
#else
        const int reportingIntervalMilliseconds = 750;
#endif
        if (!force && !changed &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_lastSystemUpdate, now).TotalMilliseconds < reportingIntervalMilliseconds)
            return;
        _lastSystemTrack = current.Track;
        _lastSystemPlaying = playing;
        _lastSystemUpdate = now;
        _systemPlayback.Publish(new SystemPlaybackState(current.Track, current.Position, playing,
            !_recommendations && (_selected is not null || _queue.Next is not null), true));
    }
}
