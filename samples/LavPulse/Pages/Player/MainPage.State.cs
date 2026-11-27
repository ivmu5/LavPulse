using Laveyana.LavPulse.Controls;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private void UpdateQueue()
    {
        UpdateQueueAnalysis();
        if (IsPlayerVisible)
        {
            _view.ShowQueue(_queue.Tracks, _queue.Current, _queue.PlayedCount, ReadQueueItem);
        }
        UpdateTransport();
    }

    private QueueTrackItem ReadQueueItem(TrackFile file)
    {
        if (!_closed && !_library.HasQueueMetadataRequest(file)) _ = ReadQueueTagsAsync(file);
        var track = _library.GetPrepared(file);
        var tags = _library.GetMetadata(file);
        return new(track?.Title ?? tags?.Title ?? Path.GetFileNameWithoutExtension(file.Name),
            track?.Artist ?? tags?.Artist, tags?.Artwork ?? default, _library.IsPreparing(file))
        {
            IsLoaded = track is not null,
            PlaybackState = QueueRowPlaybackState(file,track)
        };
    }

    private QueuePlaybackState QueueRowPlaybackState(TrackFile file,CachedTrack? track)
    {
        if(track is null || _worker is null) return QueuePlaybackState.None;
        var snapshot=_playback.PresentedSnapshot;
        if(ReferenceEquals(snapshot.Incoming?.Track,track)) return QueuePlaybackState.Starting;
        if(ReferenceEquals(snapshot.Current?.Track,track))
            return snapshot.Incoming is not null ? QueuePlaybackState.Ending : QueuePlaybackState.Playing;
        return QueuePlaybackState.None;
    }

    private void UpdateTransport()
    {
        var snapshot = _playback.Snapshot;
        if (IsPlayerVisible)
        {
            bool idle = !_closed && !IsTransitioning(snapshot) && !IsStopping;
            _view.SetTransport(!_closed && snapshot.Current.HasValue && !IsStopping,
                _worker is not null && _stop?.IsCancellationRequested == false,
                idle && !_recommendations && _selected is not null, idle && snapshot.Current.HasValue, _settings.AutoMix);
            _view.SetRepeatOne(_repeatOne);
        }
        UpdateSystemPlayback(snapshot.Current, updateCommands: true);
    }

    private async Task AwaitImportSavesAsync()
    {
        await _importGate.WaitAsync();
        _importGate.Release();
        await _saveQueue;
    }

    /// <summary>Stops output, finishes the queue save and releases playback readers.</summary>
    internal async Task CloseAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _view.TryDismissActionOverlay();
        _trackStore.TrackStorageChanged -= OnTrackStorageChanged;
        try { SaveOpenEqualizer(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        if (_view.Panel is TrackLibraryPanel library) library.Cancel();
        _lifetime.Cancel();
        SetSelection(null, null);
        _positions.Stop();
        _view.ResetAnimations();
        StopPlayback();
        try
        {
            await Task.WhenAll(_worker ?? Task.CompletedTask, _coverAnimation ?? Task.CompletedTask, _analysisWorker,
                _restoreQueue ?? Task.CompletedTask, AwaitImportSavesAsync(), _pendingBeatWrites);
        }
        catch (Exception)
        {
            // Cleanup must also run after cancellation or an output failure.
        }
        finally
        {
            _systemPlayback.CommandReceived -= OnSystemPlaybackCommand;
            try { _systemPlayback.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            // All implementations Dispose by stopping their integration.
            // Always release the independent audio/library resources as well.
            try { _playback.Dispose(); }
            finally
            {
                try { await _library.CloseAsync(); }
                finally { _lifetime.Dispose(); }
            }
        }
    }
}
