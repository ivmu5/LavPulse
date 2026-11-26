using Laveyana.LavPulse.Tracks;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private bool _canOpenSystemTracks;
    private bool _openingSystemTracks;
    private Laveyana.Audio.Playback.TrackFile? _restoredActivationFallback;

    internal void ActivateSystemTrackRequests()
    {
        _canOpenSystemTracks = true;
        if (_localQueueReady) ProcessSystemTrackRequests();
        else _restoreQueue ??= RestoreLocalQueueAsync();
    }

    internal void ProcessSystemTrackRequests()
    {
        if (_openingSystemTracks || !CanOpenSystemTracks) return;
        if (SystemTrackRequests.HasPending) _ = OpenSystemTracksAsync();
        else RestoreActivationFallback();
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
                        await LoadFirstAsync(first, autoPlay: true, fallbackToQueue: false);
                    else if (ReferenceEquals(_library.Find(current.Track), first))
                    {
                        // An explicit file open restarts that file rather than doing nothing.
                        Seek(current.Track, 0);
                        _ = PlayAsync();
                    }
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
            RestoreActivationFallback();
        }
    }

    private void RestoreActivationFallback()
    {
        if (_closed || SystemTrackRequests.HasOutstanding) return;
        var fallback = _restoredActivationFallback;
        _restoredActivationFallback = null;
        if (fallback is not null && _playback.Snapshot.Current is null && !IsLoadingFirst)
            _ = LoadFirstAsync(fallback);
    }
}
