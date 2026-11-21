using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: state workflow.</summary>
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
            IsLoaded = track is not null
        };
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
                _restoreQueue ?? Task.CompletedTask, AwaitImportSavesAsync());
        }
        catch (Exception)
        {
            // Cleanup must also run after cancellation or an output failure.
        }
        finally
        {
            _systemPlayback.CommandReceived -= OnSystemPlaybackCommand;
            _systemPlayback.Stop();
            _systemPlayback.Dispose();
            _playback.Dispose();
            await _library.CloseAsync();
            _lifetime.Dispose();
        }
    }
}
