using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Coordinates the native player, its local library and one continuous playback queue.</summary>
public sealed class MainPage : ContentPage
{
    private readonly PlayerView _view = new();
    private readonly QueuePlaybackSession _playback = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PlayerLibrary _library;
    private readonly IDispatcherTimer _positions;
    private readonly PlaybackQueue _queue = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Task? _coverAnimation;
    private TrackFile? _selected;
    private TrackFile? _armed;
    private CachedTrack? _shownCurrent;
    private CachedTrack? _shownIncoming;
    private PlayerSettings _settings = PlayerPreferences.Load();
    private bool _recommendations;
    private bool _openingSettings;
    private bool _pausing;
    private bool _pickerOpen;
    private bool _loadingFirst;
    private bool _queueRefreshPending;
    private bool _closed;

    private bool IsStopping => _pausing || _stop?.IsCancellationRequested == true;

    public MainPage()
    {
        _library = new PlayerLibrary(new TrackPreparation(FileSystem.CacheDirectory), _lifetime.Token);
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.None, SafeAreaRegions.Container, SafeAreaRegions.None, SafeAreaRegions.Container);
        BackgroundColor = _view.BackgroundColor;
        Content = _view;

        _view.PlayRequested += async () => await TogglePlaybackAsync();
        _view.NextRequested += async () => await StartMixAsync();
        _view.PreviousRequested += async () => await PreviousAsync();
        _view.AutoMixChanged += ToggleAutoMix;
        _view.SettingsRequested += async () => await OpenSettingsAsync();
        _view.RecommendationsSelected += SelectRecommendations;
        _view.QueueRequested += async file => await ChooseAsync(file);
        _view.HistoryRequested += async file => await ReplayHistoryAsync(file);
        _view.QueueMoveRequested += async (from, to, after) => await MoveQueueAsync(from, to, after);
        _view.BindFileDrops(() => !_closed, AddFilesAsync);
        _view.CurrentDeck.SeekRequested += Seek;
        _view.IncomingDeck.SeekRequested += Seek;
        _view.CurrentDeck.SpeedRequested += SetTrackSpeed;
        _view.IncomingDeck.SpeedRequested += SetTrackSpeed;

        _positions = Dispatcher.CreateTimer();
        _positions.Interval = TimeSpan.FromMilliseconds(33);
        _positions.Tick += (_, _) => UpdatePositions();
        ApplySettings(_settings, save: false);
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

        var added = _library.Add(files);
        _queue.AddRange(added);
        foreach (var file in added)
        {
            _ = ReadQueueTagsAsync(file);
        }
        UpdateQueue();
        if (_playback.Snapshot.Current is null && !_loadingFirst)
        {
            await LoadFirstAsync();
        }
        else
        {
            await SelectDefaultAsync();
        }
    }

    /// <summary>Attempts the first playable file and keeps later candidates available.</summary>
    private async Task LoadFirstAsync()
    {
        _loadingFirst = true;
        try
        {
            foreach (var file in _queue.Upcoming.ToArray())
            {
                if (_closed)
                {
                    return;
                }
                if (await PrepareAsync(file) is not { } track)
                {
                    _queue.Remove(file);
                    continue;
                }

                if (_closed)
                {
                    return;
                }
                _playback.SetFirst(track);
                _queue.Remove(file);
                ShowCurrent(_playback.Snapshot.Current!.Value);
                break;
            }
        }
        finally
        {
            _loadingFirst = false;
        }

        if (!_closed)
        {
            await SelectDefaultAsync();
        }
    }

    /// <summary>Uses the first queued track as the default transition target.</summary>
    private Task SelectDefaultAsync()
    {
        UpdateQueue();
        return _selected is null && _playback.Snapshot.Current.HasValue && _queue.Next is { } file
            ? SelectNextAsync(file) : Task.CompletedTask;
    }

    /// <summary>Selects a candidate on the first press and mixes into it on the next press.</summary>
    private async Task ChooseAsync(TrackFile file)
    {
        if (_closed || _recommendations || IsTransitioning(_playback.Snapshot))
        {
            return;
        }

        if (_playback.Snapshot.Current is not { } current)
        {
            if (!_loadingFirst)
            {
                _queue.Select(file);
                await LoadFirstAsync();
            }
            return;
        }

        if (ReferenceEquals(current.Track, _library.GetPrepared(file)))
        {
            return;
        }

        if (ReferenceEquals(_selected, file))
        {
            await StartMixAsync();
        }
        else
        {
            await SelectNextAsync(file);
        }
    }

    /// <summary>Reorders the queue immediately and only arms a candidate after preparation succeeds.</summary>
    private async Task SelectNextAsync(TrackFile file, bool renderQueue = true)
    {
        if (!TrySetSelection(file))
        {
            return;
        }
        _queue.Select(file);
        if (renderQueue)
        {
            UpdateQueue();
        }
        await ArmAsync(file);
    }

    private bool TrySetSelection(TrackFile file)
    {
        if (!_playback.SetNext(null))
        {
            return false;
        }
        _armed = null;
        _selected = file;
        return true;
    }

    /// <summary>Reorders the upcoming queue and replaces the armed candidate only if its head changes.</summary>
    private async Task MoveQueueAsync(TrackFile from, TrackFile to, bool after)
    {
        if (_closed || _loadingFirst || _recommendations || IsTransitioning(_playback.Snapshot) ||
            !_queue.Move(from, to, after, TrySetSelection))
        {
            return;
        }
        UpdateQueue();
        if (_selected is { } next && !ReferenceEquals(_armed, next))
        {
            await ArmAsync(next);
        }
    }

    /// <summary>Starts at a played entry and schedules the later history tracks before the existing queue.</summary>
    private async Task ReplayHistoryAsync(TrackFile file)
    {
        if (_closed || _recommendations || IsStopping || IsTransitioning(_playback.Snapshot) ||
            !_queue.Played.Contains(file) || !TrySetSelection(file))
        {
            return;
        }
        _queue.ReplayFrom(file);
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
        if (await PrepareAsync(file) is not { } track || _closed || !ReferenceEquals(_selected, file))
        {
            return false;
        }
        if (!_playback.SetNext(track))
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

    /// <summary>Prepares PCM, artwork and both waveforms without blocking the UI thread.</summary>
    private async Task<CachedTrack?> PrepareAsync(TrackFile file)
    {
        try
        {
            var task = _library.PrepareAsync(file);
            if (!task.IsCompleted)
            {
                ScheduleQueueRefresh();
            }
            return await task;
        }
        catch (OperationCanceledException) when (_closed)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Не удалось загрузить {file.Name}. {ex.Message}";
            return null;
        }
        finally
        {
            ScheduleQueueRefresh();
        }
    }

    private async Task ReadQueueTagsAsync(TrackFile file)
    {
        try
        {
            await _library.ReadMetadataAsync(file);
        }
        catch (Exception ex)
        {
            // Keep the filename available if optional tags cannot be read.
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            ScheduleQueueRefresh();
        }
    }

    /// <summary>Coalesces bursts of tag updates when a folder is added.</summary>
    private void ScheduleQueueRefresh()
    {
        if (_closed || _queueRefreshPending)
        {
            return;
        }
        _queueRefreshPending = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
        {
            _queueRefreshPending = false;
            if (!_closed) UpdateQueue();
        });
    }

    /// <summary>Requests the pending mix and starts the output if the player was paused.</summary>
    private async Task StartMixAsync()
    {
        if (_closed || _recommendations || IsTransitioning(_playback.Snapshot) || IsStopping ||
            _selected is not { } file || !await ArmAsync(file) || _closed || _recommendations || IsStopping || !_playback.RequestMix())
        {
            return;
        }
        UpdatePositions();
        UpdateTransport();
        if (_worker is null)
        {
            await PlayAsync(restartAtEnd: false);
        }
    }

    /// <summary>Restarts the current track or mixes into the most recently completed track.</summary>
    private async Task PreviousAsync()
    {
        var snapshot = _playback.Snapshot;
        if (_closed || IsStopping || IsTransitioning(snapshot) || snapshot.Current is not { } current)
        {
            return;
        }
        if (_recommendations || current.Position > 3 * PlaybackFormat.SampleRate || _queue.Played.LastOrDefault() is not { } previous)
        {
            Seek(current.Track, 0);
            return;
        }
        await ReplayHistoryAsync(previous);
    }

    /// <summary>Commits a seek preview to a still-active deck.</summary>
    private void Seek(CachedTrack track, long frame)
    {
        if (!_closed && !IsStopping && !IsTransitioning(_playback.Snapshot) && _playback.Seek(track, frame))
        {
            UpdatePositions();
        }
    }

    private void SetTrackSpeed(CachedTrack track, double speed)
    {
        if (!_closed && !IsStopping && !IsTransitioning(_playback.Snapshot))
        {
            _playback.TrySetSpeed(track, speed);
        }
        UpdatePositions();
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

    /// <summary>Keeps the cursor and pending transition when playback is paused and resumed.</summary>
    private async Task PlayAsync(bool restartAtEnd = true)
    {
        if (_closed || _worker is not null || _playback.Snapshot.Current is not { } current)
        {
            return;
        }

        if (restartAtEnd && current.Position >= current.Track.Waveform.TotalFrames)
        {
            _playback.Seek(current.Track, 0);
        }

        using var stop = new CancellationTokenSource();
        _stop = stop;
        _positions.Start();
        _view.Spectrum.Start(() => _playback.Spectrum);
        _view.Status.Text = "";
        _worker = _playback.RunAsync(new PlatformAudioOutput(), _settings.FadeInMilliseconds, stop.Token);
        UpdateTransport();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The window lifecycle can cancel output immediately.
        }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Ошибка воспроизведения. {ex.Message}";
        }
        finally
        {
            _worker = null;
            _stop = null;
            _pausing = false;
            _positions.Stop();
            _view.Spectrum.Stop();
            if (!_closed)
            {
                UpdatePositions();
                UpdateTransport();
            }
        }
    }

    internal void StopPlayback()
    {
        if (_stop is { IsCancellationRequested: false } stop)
        {
            stop.Cancel();
            _view.Spectrum.Stop();
            if (!_closed) UpdateTransport();
        }
    }

    /// <summary>Updates inexpensive position visuals and starts cover animations only on deck changes.</summary>
    private void UpdatePositions()
    {
        var snapshot = _playback.Snapshot;
        UpdateDecks(snapshot);
        if (snapshot.Current is not { } current)
        {
            return;
        }

        if (!ReferenceEquals(current.Track, _shownCurrent))
        {
            if (_shownCurrent is { } old && _library.Find(old) is { } outgoing)
            {
                _queue.MarkPlayed(outgoing);
            }
            bool animate = ReferenceEquals(_shownIncoming, current.Track);
            _shownIncoming = null;
            _shownCurrent = current.Track;
            _selected = _armed = null;
            if (_library.Find(current.Track) is { } file)
            {
                _queue.Remove(file);
            }
            if (animate)
            {
                _coverAnimation = FinishCoverAsync(current.Track);
            }
            else
            {
                _view.ShowCurrent(current.Track);
                _ = SelectDefaultAsync();
            }
            UpdateTransport();
        }

        if (snapshot.Incoming is { } incoming && !ReferenceEquals(_shownIncoming, incoming.Track))
        {
            _shownIncoming = incoming.Track;
            _ = _view.BeginMixAsync(incoming.Track, _library.Find(incoming.Track));
            UpdateTransport();
        }
    }

    /// <summary>Preloads the next track while the covers move between their final positions.</summary>
    private async Task FinishCoverAsync(CachedTrack track)
    {
        var animation = _view.FinishMixAsync(track, UpdateQueue);
        var preparation = _queue.Next is { } next ? SelectNextAsync(next, renderQueue: false) : Task.CompletedTask;
        await animation;
        if (!_closed)
        {
            UpdateDecks(_playback.Snapshot);
        }
        await preparation;
        if (!_closed)
        {
            UpdateQueue();
        }
    }

    /// <summary>Keeps scrubbing locked from the mix request through the cover handoff.</summary>
    private bool IsTransitioning(QueueSnapshot snapshot) =>
        snapshot.IsMixing || _shownIncoming is not null || _view.IsHandingOff;

    private void UpdateDecks(QueueSnapshot snapshot)
    {
        bool transitioning = IsTransitioning(snapshot);
        _view.ShowDecks(snapshot, !_closed && !IsStopping && !transitioning, showOverview: !transitioning);
    }

    private void ShowCurrent(PlayingTrack track)
    {
        _shownCurrent = track.Track;
        _view.ShowCurrent(track.Track);
        _view.CurrentDeck.Show(track, true);
    }

    private void ToggleAutoMix() => ApplySettings(_settings with { AutoMix = !_settings.AutoMix });

    private void SelectRecommendations(bool enabled)
    {
        _recommendations = enabled;
        _playback.SetQueueEnabled(!enabled);
        UpdateTransport();
    }

    private async Task OpenSettingsAsync()
    {
        if (_closed || _openingSettings || Navigation.ModalStack.Count > 0)
        {
            return;
        }

        _openingSettings = true;
        try
        {
            await Navigation.PushModalAsync(new PlayerSettingsPage(_settings, value => ApplySettings(value), PickFilesAsync, PickFolderAsync));
        }
        finally
        {
            _openingSettings = false;
        }
    }

    private void ApplySettings(PlayerSettings value, bool save = true)
    {
        _settings = value;
        _playback.SetAutoMix(value.AutoMix);
        _playback.SetCrossfadeDuration(value.CrossfadeMilliseconds);
        _view.ApplyAppearance(value);
        if (save)
        {
            PlayerPreferences.Save(value);
        }
        UpdateQueue();
    }

    private void UpdateQueue()
    {
        _view.ShowQueue(_queue.Upcoming, _queue.Played, ReadQueueItem);
        UpdateTransport();
    }

    private QueueTrackItem ReadQueueItem(TrackFile file)
    {
        var track = _library.GetPrepared(file);
        var tags = track is null ? _library.GetMetadata(file) : null;
        return new(track?.Title ?? tags?.Title ?? Path.GetFileNameWithoutExtension(file.Name),
            track?.Artist ?? tags?.Artist, track?.Artwork ?? tags?.Artwork ?? default, _library.IsAnalyzing(file));
    }

    private void UpdateTransport()
    {
        var snapshot = _playback.Snapshot;
        bool idle = !_closed && !IsTransitioning(snapshot) && !IsStopping;
        _view.SetTransport(!_closed && snapshot.Current.HasValue && !IsStopping,
            _worker is not null && _stop?.IsCancellationRequested == false,
            idle && !_recommendations && _selected is not null, idle && snapshot.Current.HasValue, _settings.AutoMix);
    }

    /// <summary>Stops output before closing readers and clears temporary decoded tracks.</summary>
    internal async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _lifetime.Cancel();
        _positions.Stop();
        _view.CurrentDeck.CancelScrub();
        _view.ResetAnimations();
        StopPlayback();
        try
        {
            await Task.WhenAll(_worker ?? Task.CompletedTask, _coverAnimation ?? Task.CompletedTask);
        }
        catch (Exception)
        {
            // Cleanup must also run after cancellation or an output failure.
        }
        finally
        {
            _playback.Dispose();
            await _library.CloseAsync();
            _lifetime.Dispose();
        }
    }
}
