using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Partial player coordinator: analysis workflow.</summary>
public sealed partial class MainPage
{
    /// <summary>Queues each newly added MP3 once. Only one background decoder is fed at a time.</summary>
    private void EnqueueBackgroundAnalysis(IEnumerable<TrackFile> files)
    {
        foreach (var file in files)
        {
            if (_analysisSeen.Add(file)) _analysisPending.Enqueue(file);
        }
        if (_closed || _analysisRunning || _analysisPending.Count == 0) return;
        _analysisRunning = true;
        _analysisWorker = AnalyzeQueueSequentiallyAsync();
    }

    /// <summary>
    /// Prepares the queue from left to right using one background lane. Direct selections use
    /// the second decode slot, so a large imported folder never blocks user-initiated playback.
    /// </summary>
    private async Task AnalyzeQueueSequentiallyAsync()
    {
        try
        {
            while (!_closed && _analysisPending.TryDequeue(out var file))
            {
                if (!_queue.Tracks.Contains(file)) continue;
                try
                {
                    // The library deduplicates concurrent preparation with foreground requests.
                    if (_library.GetPrepared(file) is null)
                    {
                        var work = _library.PrepareAsync(file);
                        if (!work.IsCompleted) ScheduleQueueRefresh();
                        await work;
                    }
                }
                catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One damaged MP3 must not prevent the rest of the queue from being analyzed.
                    System.Diagnostics.Debug.WriteLine($"LavPulse background analysis: {file.Name}: {ex}");
                }
                finally
                {
                    ScheduleQueueRefresh();
                }

                // Avoid sustained disk/CPU saturation while the mixer is active.
                await Task.Delay(100, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_closed || _lifetime.IsCancellationRequested)
        {
            // Closing the player cancels background work before cache files are deleted.
        }
        finally
        {
            _analysisRunning = false;
        }
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
}
