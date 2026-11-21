using System.Diagnostics;
using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: import workflow.</summary>
public sealed partial class MainPage
{
    private const int ImportBatchSize = 16;
    private static readonly TimeSpan ImportCheckpointInterval = TimeSpan.FromMilliseconds(500);

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
                    var (requested, _) = await ImportLocalTracksAsync(files);
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

    private bool OpenTrackLibrary()
    {
        if (_pickerOpen) return false;
        var library = OpenPanel(() => new TrackLibraryPanel(
            () => Window is { } window ? WithPickerAsync(() => PlatformTrackFolder.PickAsync(window, _lifetime.Token))
                : Task.FromResult<TrackFolderSource?>(null),
            OpenSavedLibraryAsync, ImportLibrarySelectionAsync, _library.ReadLibraryMetadataAsync,
            ListLibraryScanTracksAsync, RescanLibraryTrackAsync, RemoveLibrarySelectionAsync, ClosePanel, _lifetime.Token,
            _trackStore.ReadTrackStorageSizeAsync));
        if (library is null) return false;
        library.ImportFolderOpened += RecordOpeningScreen;
        library.ImportFolderClosed += root =>
            _forwardNavigation.Push(() => _view.Panel is TrackLibraryPanel panel && panel.TryReopenImportFolder(root));
        _ = library.InitializeAsync();
        return true;
    }

    private void OnTrackStorageChanged(string path) => Dispatcher.Dispatch(() =>
    {
        if (!_closed && _view.Panel is TrackLibraryPanel library) library.RefreshTrackStorageSize(path);
    });

    private async Task<TrackFolderSource> OpenSavedLibraryAsync()
    {
        await (_restoreQueue ??= RestoreLocalQueueAsync());
        var entries = _trackStore.LibrarySnapshot();
        return await Task.Run(() => _trackStore.OpenLibrary(entries), _lifetime.Token);
    }

    private async Task<int> ImportLibrarySelectionAsync(TrackLibrarySelection selection, bool addToQueue)
    {
        var folders = selection.Folders;
        var files = selection.File is { } file
            ? (IReadOnlyList<FolderTrack>)[new(file, folders.Select(folder => new StoredFolder(folder.Id, folder.Name)).ToArray())]
            : await TrackFolderReader.ReadTracksAsync(folders[^1], _lifetime.Token,
                folders.Take(folders.Count - 1).ToArray());
        var (_, updated) = await ImportLocalTracksAsync(files, loadFirst: addToQueue, alreadyLocal: selection.Local,
            addToQueue: addToQueue);
        if (addToQueue && !_closed && !IsLoadingFirst) await SelectDefaultAsync();
        return updated;
    }

    private static async Task<IReadOnlyList<TrackFile>> ListLibraryScanTracksAsync(TrackLibrarySelection selection,
        CancellationToken token) => selection.File is { } file ? [file] :
        (await TrackFolderReader.ReadTracksAsync(selection.Folders[^1], token))
        .Select(item => item.File).DistinctBy(track => track.LocalPath).ToArray();

    private Task RescanLibraryTrackAsync(TrackFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return _library.RescanAsync(_library.Add([file], [])[0], token);
    }

    private async Task<int> RemoveLibrarySelectionAsync(TrackLibrarySelection selection)
    {
        if (_closed || !selection.Local) return 0;
        await _importGate.WaitAsync(_lifetime.Token);
        try
        {
            int removed = _trackStore.ForgetLibraryItem(selection.Folders[^1].Id, selection.File);
            if (removed > 0) await SaveQueueAsync();
            return removed;
        }
        finally { _importGate.Release(); }
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
        if (current is not null && !_queue.Contains(current)) current = _queue.Next;
        _analysisQueue = _queue.Tracks.ToArray();
        var snapshot = _trackStore.Snapshot(_analysisQueue, current);
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

    private Task<(IReadOnlyList<TrackFile> Tracks, int Updated)> ImportLocalTracksAsync(IReadOnlyList<TrackFile> files, bool loadFirst = false) =>
        ImportLocalTracksAsync(files.Select(file => new FolderTrack(file, [])).ToArray(), loadFirst);

    private async Task<(IReadOnlyList<TrackFile> Tracks, int Updated)> ImportLocalTracksAsync(IReadOnlyList<FolderTrack> files, bool loadFirst,
        bool alreadyLocal = false, bool addToQueue = true)
    {
        if (_closed) return ([], 0);
        var token = _lifetime.Token;
        var requested = new List<TrackFile>();
        int updated = 0;
        try
        {
            await (_restoreQueue ??= RestoreLocalQueueAsync());
            await _importGate.WaitAsync(token);
            int pending = 0;
            long lastCheckpoint = Stopwatch.GetTimestamp();
            try
            {
                foreach (var item in files)
                {
                    token.ThrowIfCancellationRequested();
                    var file = item.File;
                    if (!TrackFile.IsMp3(file.Name)) continue;
                    TrackFile local;
                    try { local = alreadyLocal ? file : await Task.Run(() => _trackStore.CopyAsync(file, token), token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _view.Status.Text = $"Не удалось сохранить {file.Name}. {ex.Message}";
                        continue;
                    }
                    if (_closed) break;
                    var canonical = _library.Add([local], [])[0];
                    bool remember = !alreadyLocal && _trackStore.Remember(local, item.Folders);
                    bool add = addToQueue && !_queue.Contains(canonical);
                    if (add)
                    {
                        _queue.AddRange([canonical]);
                    }
                    requested.Add(canonical);
                    if (!add && !remember) continue;
                    pending++;
                    if (addToQueue ? add : remember) updated++;
                    bool first = add && loadFirst && _playback.Snapshot.Current is null && !IsLoadingFirst;
                    if (!first && pending < ImportBatchSize &&
                        Stopwatch.GetElapsedTime(lastCheckpoint) < ImportCheckpointInterval) continue;
                    await FlushAsync();
                    if (first && !_closed && _playback.Snapshot.Current is null && !IsLoadingFirst)
                        _ = LoadFirstAsync();
                }
            }
            finally
            {
                try { if (pending > 0) await FlushAsync(); }
                finally { _importGate.Release(); }
            }

            async Task FlushAsync()
            {
                pending = 0;
                lastCheckpoint = Stopwatch.GetTimestamp();
                var save = SaveQueueAsync();
                if (addToQueue && !_closed) UpdateQueue();
                await save;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return (requested, updated);
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
