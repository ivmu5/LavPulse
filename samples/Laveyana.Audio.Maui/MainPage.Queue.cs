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
            await LoadFirstAsync(preferred: file);
            return;
        }

        if (ReferenceEquals(current.Track, _library.GetPrepared(file)))
            return;

        // An unprepared selection is armed after decoding. A newer selection wins the race.
        if (!TrySetSelection(file, byUser: true))
            return;
        UpdateQueue();
        await StartMixAsync();
    }

    private bool TrySetSelection(TrackFile? file, bool byUser = false)
    {
        var prepared = file is null ? null : _library.GetPrepared(file, forPlayback: true);
        if (_shownCurrent is not { } current || !_playback.SetNext(prepared, current))
        {
            return false;
        }
        SetSelection(file, prepared is null ? null : file, byUser);
        return true;
    }

    private void SetSelection(TrackFile? file, TrackFile? armed, bool byUser = false)
    {
        var previous = _selectionLoad;
        bool changed = !ReferenceEquals(_selected, file);
        if (changed)
        {
            _selectionLoad = file is null ? null : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
        _selected = file;
        _armed = armed;
        _selectedByUser = byUser;
        if (changed)
        {
            previous?.Cancel();
            previous?.Dispose();
        }
    }

    private bool UpdateQueueSuccessor(TrackFile? next) =>
        _shownCurrent is null ||
        (_selectedByUser && _selected is { } file && _queue.Tracks.Contains(file)) ||
        TrySetSelection(next);

    /// <summary>Moves a row while keeping playback on the same track.</summary>
    private async Task MoveQueueAsync(TrackFile from, TrackFile to, bool after)
    {
        if (_closed || _recommendations || IsTransitioning(_playback.Snapshot) ||
            !_queue.Move(from, to, after, UpdateQueueSuccessor))
        {
            return;
        }
        _ = SaveQueueAsync();
        UpdateQueue();
        if (_selected is { } next && !ReferenceEquals(_armed, next))
        {
            await ArmAsync(next);
        }
    }

    private async Task RemoveQueueTrackAsync(TrackFile file)
    {
        if (_closed || _recommendations || IsTransitioning(_playback.Snapshot) ||
            !_queue.TryRemove(file, UpdateQueueSuccessor, refreshSuccessor: ReferenceEquals(file, _selected)))
        {
            return;
        }
        if (_repeatOne && ReferenceEquals(file, _queue.Current))
        {
            ToggleRepeatOne();
        }
        _ = SaveQueueAsync();
        UpdateQueue();
        if (ReferenceEquals(file, _firstLoadFile))
        {
            await LoadFirstAsync();
            return;
        }
        if (_selected is { } nextTrack && !ReferenceEquals(_armed, nextTrack))
        {
            await ArmAsync(nextTrack);
        }
    }

    /// <summary>Jumps back to the selected entry without rearranging the playlist.</summary>
    private async Task ReplayHistoryAsync(TrackFile file)
    {
        if (_closed || _recommendations || IsStopping || IsTransitioning(_playback.Snapshot) ||
            !_queue.IsBeforeCurrent(file) || !TrySetSelection(file, byUser: true))
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
        if (!ReferenceEquals(_selected, file) || _selectionLoad is not { } request)
        {
            return false;
        }
        var token = request.Token;
        if (await PrepareAsync(file, token) is not { } track || _closed || token.IsCancellationRequested ||
            !ReferenceEquals(_selected, file) || !_queue.Tracks.Contains(file) || _shownCurrent is not { } current)
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
