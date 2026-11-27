using Laveyana.LavPulse.Controls;
using Laveyana.LavPulse.Tracks;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private bool OpenTrackLibrary()
    {
        if (_pickerOpen) return false;
        var library = OpenPanel(() => new TrackLibraryPanel(
            () => Window is { } window ? WithPickerAsync(() => PlatformTrackFolder.PickAsync(window, _lifetime.Token))
                : Task.FromResult<TrackFolderSource?>(null),
            OpenSavedLibraryAsync, ImportLibrarySelectionAsync, _library.ReadLibraryMetadataAsync,
            ListLibraryScanTracksAsync, ScanLibraryTrackAsync, _library.HasAnalysis, _settings.AnalysisParallelism,
            RemoveLibrarySelectionAsync, ClosePanel, _lifetime.Token,
            _trackStore.ReadTrackStorageSizeAsync, _settings.ScanReturnSeconds));
        if (library is null) return false;
        library.ConfigureListActions(_settings,ShowLibraryActionsAsync,ExecuteLibrarySwipeAsync);
        library.SelectionActionRequested = ExecuteLibrarySelectionActionAsync;
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
        if (addToQueue && !_closed && !IsLoadingFirst) _ = SelectDefaultAsync();
        return updated;
    }

    private static async Task<IReadOnlyList<TrackFile>> ListLibraryScanTracksAsync(TrackLibrarySelection selection,
        CancellationToken token) => selection.File is { } file ? [file] :
        (await TrackFolderReader.ReadTracksAsync(selection.Folders[^1], token))
        .Select(item => item.File).DistinctBy(track => track.LocalPath).ToArray();

    private Task<bool> ScanLibraryTrackAsync(TrackFile file, bool replaceAnalysis, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return _library.ScanLibraryTrackAsync(_library.Add([file], [])[0], replaceAnalysis, token);
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
}
