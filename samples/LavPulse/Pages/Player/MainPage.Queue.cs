using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private bool _autoPlayFirst;
    private bool _confirmingQueueClear;

    /// <summary>Uses the first queued track as the default transition target.</summary>
    private Task SelectDefaultAsync()
    {
        UpdateQueue();
        return _selected is null && _queue.Current is not null && _queue.Next is { } next && TrySetSelection(next)
            ? ArmAsync(next) : Task.CompletedTask;
    }

    /// <summary>Switches directly to a tapped track without changing the order of the queue.</summary>
    private async Task ChooseAsync(TrackFile file, bool autoPlay = false)
    {
        if (_closed || _recommendations || IsStopping || IsTransitioning(_playback.Snapshot) ||
            !_queue.Contains(file))
        {
            return;
        }

        if (_playback.Snapshot.Current is not { } current)
        {
            await LoadFirstAsync(preferred: file, autoPlay: autoPlay);
            return;
        }

        if (ReferenceEquals(current.Track, _library.GetPrepared(file)))
        {
            if (autoPlay) await PlayAsync();
            return;
        }

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
        (_selectedByUser && _selected is { } file && _queue.Contains(file)) ||
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

    private async Task ClearQueueAsync()
    {
        if (_closed || _confirmingQueueClear || _queue.Tracks.Count == 0) return;
        _confirmingQueueClear = true;
        try
        {
            bool confirmed = await DisplayAlertAsync("Очистить очередь?", "Треки останутся в медиатеке.",
                "Очистить", "Отмена");
            if (!confirmed || _closed || _queue.Tracks.Count == 0) return;

            // An overlap already being rendered can finish, but no new mix should start.
            _playback.SetNext(null);
            SetSelection(null, null);
            _queue.Clear();
            var firstLoad = _firstLoad;
            _firstLoad = null;
            _firstLoadFile = null;
            firstLoad?.Cancel();
            if (_repeatOne) ToggleRepeatOne();
            var save = SaveQueueAsync();
            UpdateQueue();
            await save;
        }
        finally
        {
            _confirmingQueueClear = false;
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
            !ReferenceEquals(_selected, file) || !_queue.Contains(file) || _shownCurrent is not { } current)
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

    /// <summary>Attempts the first playable file and keeps later candidates available.</summary>
    private async Task LoadFirstAsync(TrackFile? preferred = null, bool autoPlay = false)
    {
        var file = preferred ?? _queue.Tracks.FirstOrDefault();
        if (IsLoadingFirst && file is not null && ReferenceEquals(file, _firstLoadFile))
        {
            _autoPlayFirst |= autoPlay;
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _firstLoad;
        _firstLoad = request;
        _firstLoadFile = file;
        _autoPlayFirst = autoPlay;
        previous?.Cancel();
        var failed = new HashSet<TrackFile>();
        try
        {
            while (file is not null && !request.IsCancellationRequested)
            {
                _firstLoadFile = file;
                var track = await PrepareAsync(file, request.Token);
                if (_closed || request.IsCancellationRequested)
                {
                    return;
                }
                if (_queue.Contains(file))
                {
                    if (track is not null)
                    {
                        _playback.SetFirst(track);
                        _queue.SetCurrent(file);
                        _ = SaveQueueAsync();
                        ShowCurrent(_playback.Snapshot.Current!.Value);
                        break;
                    }
                    failed.Add(file);
                }
                file = _queue.Tracks.FirstOrDefault(candidate => !failed.Contains(candidate));
            }
        }
        finally
        {
            if (ReferenceEquals(_firstLoad, request))
            {
                autoPlay = _autoPlayFirst;
                _firstLoad = null;
                _firstLoadFile = null;
            }
        }

        if (!_closed && !request.IsCancellationRequested)
        {
            if (autoPlay) _ = PlayAsync();
            await SelectDefaultAsync();
            ProcessSystemTrackRequests();
        }
    }
}
