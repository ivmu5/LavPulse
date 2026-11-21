using Laveyana.Audio.Playback;
#if IOS
using UIKit;
#endif

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: analysis workflow.</summary>
public sealed partial class MainPage
{
    private const int QueueLookahead = 10;
#if IOS
    private nint _analysisBackgroundTask = UIApplication.BackgroundTaskInvalid;
    private bool _analysisBackgroundExpired;
#endif

    private void UpdateQueueAnalysis()
    {
        if (_closed) return;

        var upcoming = _queue.GetUpcoming(QueueLookahead);
        if (!IsLoadingFirst)
        {
            var snapshot = _playback.Snapshot;
            _library.Trim(snapshot.Current?.Track, snapshot.Incoming?.Track, _selected, _armed, upcoming);
        }

        // Give the initial deck its selected track before starting the lookahead.
        bool allowed = _queue.Current is not null;
#if IOS
        if (_windowVisible) _analysisBackgroundExpired = false;
        allowed &= !_analysisBackgroundExpired || _worker is not null;
#endif
        var work = _queueAnalysis.Update(upcoming, allowed, _analysisQueue);
        if (!_queueAnalysis.IsRunning) return;

#if ANDROID
        if (_windowVisible) AndroidTrackAnalysisService.Start();
#elif IOS
        if (_windowVisible && _analysisBackgroundTask == UIApplication.BackgroundTaskInvalid)
        {
            _analysisBackgroundTask = UIApplication.SharedApplication.BeginBackgroundTask(
                "LavPulse track analysis", OnAnalysisBackgroundExpiration);
        }
#endif
        if (work is not null) _analysisWorker = ObserveQueueAnalysisAsync(work);
    }

#if IOS
    private void OnAnalysisBackgroundExpiration()
    {
        EndAnalysisBackgroundTask();
        _analysisBackgroundExpired = true;
        UpdateQueueAnalysis();
    }

    private void EndAnalysisBackgroundTask()
    {
        if (_analysisBackgroundTask == UIApplication.BackgroundTaskInvalid) return;
        UIApplication.SharedApplication.EndBackgroundTask(_analysisBackgroundTask);
        _analysisBackgroundTask = UIApplication.BackgroundTaskInvalid;
    }
#endif

    private async Task ObserveQueueAnalysisAsync(Task work)
    {
        try { await work; }
        finally
        {
            _queueAnalysis.Finish();
            try
            {
                // Keep the platform's background execution if the queue changed as the worker was exiting.
                if (!_closed) UpdateQueue();
            }
            finally
            {
                if (!_queueAnalysis.IsRunning)
                {
#if ANDROID
                    AndroidTrackAnalysisService.Stop();
#elif IOS
                    EndAnalysisBackgroundTask();
#endif
                }
            }
        }
    }

    private void OnQueueAnalysisProgress()
    {
        if (_windowVisible) Dispatcher.Dispatch(ScheduleQueueRefresh);
    }

    /// <summary>Prepares PCM, artwork and both waveforms without blocking the UI thread.</summary>
    private async Task<CachedTrack?> PrepareAsync(TrackFile file, CancellationToken selection)
    {
        try
        {
            var task = _library.PrepareAsync(file, selection);
            if (!task.IsCompleted)
            {
                ScheduleQueueRefresh();
            }
            return await task;
        }
        catch (OperationCanceledException) when (_closed || selection.IsCancellationRequested)
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
            await _library.ReadQueueMetadataAsync(file);
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

    /// <summary>Coalesces visible tag and preparation updates.</summary>
    private void ScheduleQueueRefresh()
    {
        if (_closed || !IsPlayerVisible || _queueRefreshPending)
        {
            return;
        }
        _queueRefreshPending = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
        {
            _queueRefreshPending = false;
            if (!_closed && IsPlayerVisible) UpdateQueue();
        });
    }
}
