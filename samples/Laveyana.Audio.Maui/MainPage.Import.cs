using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: import workflow.</summary>
public sealed partial class MainPage
{
    private bool _canOpenSystemTracks;
    private bool _openingSystemTracks;
    private bool _localQueueReady;
    private Task? _restoreQueue;
    private Task _saveQueue = Task.CompletedTask;
    private TrackFile[] _analysisQueue = [];
    private readonly SemaphoreSlim _importGate = new(1);

    internal void ActivateSystemTrackRequests()
    {
        _canOpenSystemTracks = true;
        if (_localQueueReady) ProcessSystemTrackRequests();
        else _restoreQueue ??= RestoreLocalQueueAsync();
    }

    internal void ProcessSystemTrackRequests()
    {
        if (SystemTrackRequests.HasPending && !_openingSystemTracks && CanOpenSystemTracks)
            _ = OpenSystemTracksAsync();
    }

    private bool CanOpenSystemTracks => _canOpenSystemTracks && _localQueueReady && !_closed && _windowVisible &&
        !IsLoadingFirst && !IsStopping && !IsTransitioning(_playback.Snapshot);

    private async Task OpenSystemTracksAsync()
    {
        _openingSystemTracks = true;
        try
        {
            while (CanOpenSystemTracks && SystemTrackRequests.TryTake(out var files) && files is not null)
            {
                try
                {
                    var requested = await ImportLocalTracksAsync(files);
                    if (_closed) return;
                    if (requested.Count == 0) continue;

                    _view.ShowQueueFeed();

                    var first = requested[0];
                    if (_playback.Snapshot.Current is not { } current)
                        await LoadFirstAsync(first, autoPlay: true);
                    else if (ReferenceEquals(_library.Find(current.Track), first))
                        _ = PlayAsync();
                    else if (TrySetSelection(first, byUser: true))
                        await StartMixAsync(waitForPlayback: false);
                    else
                    {
                        // The audio thread can begin a transition after the UI checks readiness.
                        SystemTrackRequests.Open(files);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    if (!_closed) _view.Status.Text = $"Не удалось открыть файл. {ex.Message}";
                }
            }
        }
        finally
        {
            _openingSystemTracks = false;
        }
    }

    /// <summary>Imports MP3s selected using the system file picker.</summary>
    private async Task PickFilesAsync()
    {
        if (await WithPickerAsync(Mp3FilePicker.PickMultipleAsync) is { Count: > 0 } files)
        {
            await AddFilesAsync(files);
        }
    }

    /// <summary>Adds the MP3s in a platform folder to the local library.</summary>
    private async Task PickFolderAsync()
    {
        if (Window is { } window && await WithPickerAsync(() => PlatformTrackFolder.PickAsync(window, _lifetime.Token)) is { } folder)
        {
            if (folder.Files.Count == 0)
            {
                _view.Status.Text = "В этой папке нет MP3.";
            }
            await AddFilesAsync(folder.Files);
        }
    }

    /// <summary>Serializes native picker launches and reports a picker error in the player.</summary>
    private async Task<T?> WithPickerAsync<T>(Func<Task<T?>> pick) where T : class
    {
        if (_closed || _pickerOpen)
        {
            return null;
        }

        _pickerOpen = true;
        try
        {
            return await pick();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Не удалось открыть файлы. {ex.Message}";
            return null;
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    private async Task RestoreLocalQueueAsync()
    {
        var token = _lifetime.Token;
        try
        {
            var restored = await Task.Run(() => _trackStore.ReadQueueAsync(token), token);
            if (_closed) return;
            _queue.AddRange(_library.Add(restored.Tracks, []));
            _analysisQueue = _queue.Tracks.ToArray();
            _localQueueReady = true;
            UpdateQueue();
            if (_queue.Tracks.Count > 0) _ = LoadFirstAsync(restored.Current);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Не удалось восстановить очередь. {ex.Message}";
        }
        finally
        {
            _localQueueReady = !_closed;
            ProcessSystemTrackRequests();
        }
    }

    private Task SaveQueueAsync()
    {
        var current = _queue.Current;
        if (current is not null && !_queue.Tracks.Contains(current)) current = _queue.Next;
        _analysisQueue = _queue.Tracks.ToArray();
        var snapshot = LocalTrackStore.Snapshot(_analysisQueue, current);
        return _saveQueue = SaveAfterAsync(_saveQueue, snapshot);
    }

    private async Task SaveAfterAsync(Task previous, StoredQueue snapshot)
    {
        try
        {
            await previous;
            await Task.Run(() => _trackStore.SaveQueueAsync(snapshot));
        }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Не удалось сохранить очередь. {ex.Message}";
        }
    }

    private async Task<IReadOnlyList<TrackFile>> ImportLocalTracksAsync(IReadOnlyList<TrackFile> files, bool loadFirst = false)
    {
        if (_closed) return [];
        var token = _lifetime.Token;
        var requested = new List<TrackFile>();
        try
        {
            await (_restoreQueue ??= RestoreLocalQueueAsync());
            await _importGate.WaitAsync(token);
            try
            {
                foreach (var file in files)
                {
                    if (!TrackFile.IsMp3(file.Name)) continue;
                    token.ThrowIfCancellationRequested();
                    TrackFile local;
                    try { local = await Task.Run(() => _trackStore.CopyAsync(file, token), token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _view.Status.Text = $"Не удалось сохранить {file.Name}. {ex.Message}";
                        continue;
                    }
                    if (_closed) break;
                    var canonical = _library.Add([local], [])[0];
                    requested.Add(canonical);
                    if (_queue.Tracks.Contains(canonical)) continue;
                    _queue.AddRange([canonical]);
                    var save = SaveQueueAsync();
                    UpdateQueue();
                    await save;
                    if (loadFirst && !_closed && _playback.Snapshot.Current is null && !IsLoadingFirst)
                        _ = LoadFirstAsync();
                }
            }
            finally { _importGate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return requested;
    }

    /// <summary>Keeps each local copy and starts analysis as the files appear in the queue.</summary>
    private async Task AddFilesAsync(IReadOnlyList<TrackFile> files)
    {
        await ImportLocalTracksAsync(files, loadFirst: true);
        if (!_closed && !IsLoadingFirst) await SelectDefaultAsync();
    }

    /// <summary>Attempts the first playable file and keeps later candidates available.</summary>
    private async Task LoadFirstAsync(TrackFile? preferred = null, bool autoPlay = false)
    {
        var file = preferred ?? _queue.Tracks.FirstOrDefault();
        if (IsLoadingFirst && file is not null && ReferenceEquals(file, _firstLoadFile))
        {
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var previous = _firstLoad;
        _firstLoad = request;
        _firstLoadFile = file;
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
                if (_queue.Tracks.Contains(file))
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
