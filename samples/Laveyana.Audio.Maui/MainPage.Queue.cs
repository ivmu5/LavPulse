using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: queue workflow.</summary>
public sealed partial class MainPage
{
    /// <summary>Uses the first queued track as the default transition target.</summary>
    private Task SelectDefaultAsync()
    {
        UpdateQueue();
        return _selected is null && _queue.Current is not null && _queue.Next is { } next && TrySetSelection(next)
            ? ArmAsync(next) : Task.CompletedTask;
    }

    /// <summary>Switches directly to a tapped track without changing the order of the queue.</summary>
    private async Task ChooseAsync(TrackFile file)
    {
        if (_closed || _recommendations || IsStopping || IsTransitioning(_playback.Snapshot) ||
            !_queue.Tracks.Contains(file))
        {
            return;
        }

        if (_playback.Snapshot.Current is not { } current)
        {
            if (!_loadingFirst) await LoadFirstAsync(preferred: file);
            return;
        }

        if (ReferenceEquals(current.Track, _library.GetPrepared(file)))
            return;

        // An unprepared selection is armed after decoding. A newer selection wins the race.
        if (!TrySetSelection(file))
            return;
        UpdateQueue();
        await StartMixAsync();
    }

    private bool TrySetSelection(TrackFile? file)
    {
        var prepared = file is null ? null : _library.GetPrepared(file, forPlayback: true);
        if (_shownCurrent is not { } current || !_playback.SetNext(prepared, current))
        {
            return false;
        }
        _armed = prepared is null ? null : file;
        _selected = file;
        return true;
    }

    /// <summary>Moves a row while keeping playback on the same track.</summary>
    private async Task MoveQueueAsync(TrackFile from, TrackFile to, bool after)
    {
        if (_closed || _loadingFirst || _recommendations || IsTransitioning(_playback.Snapshot) ||
            !_queue.Move(from, to, after, file => _shownCurrent is null || TrySetSelection(file)))
        {
            return;
        }
        UpdateQueue();
        if (_selected is { } next && !ReferenceEquals(_armed, next))
        {
            await ArmAsync(next);
        }
    }

    private async Task RemoveQueueTrackAsync(TrackFile file)
    {
        if (_closed || _loadingFirst || _recommendations || IsTransitioning(_playback.Snapshot) ||
            !_queue.TryRemove(file, next => _shownCurrent is null || TrySetSelection(next)))
        {
            return;
        }
        if (_repeatOne && ReferenceEquals(file, _queue.Current))
        {
            ToggleRepeatOne();
        }
        UpdateQueue();
        if (_selected is { } nextTrack && !ReferenceEquals(_armed, nextTrack))
        {
            await ArmAsync(nextTrack);
        }
    }

    /// <summary>Jumps back to the selected entry without rearranging the playlist.</summary>
    private async Task ReplayHistoryAsync(TrackFile file)
    {
        if (_closed || _recommendations || IsStopping || IsTransitioning(_playback.Snapshot) ||
            !_queue.IsBeforeCurrent(file) || !TrySetSelection(file))
        {
            return;
        }
        UpdateQueue();
        await StartMixAsync();
    }

    /// <summary>Preloads a candidate and ignores earlier selections that finish out of order.</summary>
    private async Task<bool> ArmAsync(TrackFile file)
    {
        if (ReferenceEquals(_armed, file))
        {
            return true;
        }
        if (await PrepareAsync(file) is not { } track || _closed || !ReferenceEquals(_selected, file) || _shownCurrent is not { } current)
        {
            return false;
        }
        if (!_playback.SetNext(track, current))
        {
            return false;
        }

        _armed = file;
        if (!_view.IsHandingOff)
        {
            UpdateQueue();
        }
        return true;
    }
}
