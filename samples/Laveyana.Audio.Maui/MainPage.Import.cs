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

    internal void ActivateSystemTrackRequests()
    {
        _canOpenSystemTracks = true;
        ProcessSystemTrackRequests();
    }

    internal void ProcessSystemTrackRequests()
    {
        if (SystemTrackRequests.HasPending && !_openingSystemTracks && CanOpenSystemTracks)
            _ = OpenSystemTracksAsync();
    }

    private bool CanOpenSystemTracks => _canOpenSystemTracks && !_closed && _windowVisible &&
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
                    var requested = _library.Add(files, []);
                    if (requested.Count == 0) continue;

                    _view.ShowQueueFeed();
                    var queued = new HashSet<TrackFile>(_queue.Tracks);
                    _queue.AddRange(requested.Where(queued.Add));
                    UpdateQueue();

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

    /// <summary>Loads the initial track and preloads the following choice in the background.</summary>
    private async Task AddFilesAsync(IReadOnlyList<TrackFile> files)
    {
        if (_closed)
        {
            return;
        }

        var added = _library.Add(files, _queue.Tracks);
        _queue.AddRange(added);
        UpdateQueue();
        if (_playback.Snapshot.Current is null && !IsLoadingFirst)
        {
            await LoadFirstAsync();
        }
        else
        {
            await SelectDefaultAsync();
        }
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
                        ShowCurrent(_playback.Snapshot.Current!.Value);
                        break;
                    }
                    _queue.Remove(file);
                }
                file = _queue.Tracks.FirstOrDefault();
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
