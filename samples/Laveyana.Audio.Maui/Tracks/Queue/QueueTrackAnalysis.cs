using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Prepares the current lookahead one track at a time, independently of the UI dispatcher.</summary>
internal sealed class QueueTrackAnalysis(Func<TrackFile, bool> isPrepared, Func<TrackFile, Task> prepare,
    Action progress, CancellationToken lifetime,
    Func<TrackFile, bool>? isAnalyzed = null, Func<TrackFile, Task>? analyze = null)
{
    private readonly object _gate = new();
    private readonly HashSet<TrackFile> _failed = [];
    private TrackFile[] _upcoming = [];
    private TrackFile[] _queue = [];
    private bool _allowed;
    private bool _running;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public Task? Update(TrackFile[] upcoming, bool allowed, TrackFile[]? queue = null)
    {
        lock (_gate)
        {
            queue ??= [];
            bool changed = !_upcoming.SequenceEqual(upcoming) ||
                (!ReferenceEquals(_queue, queue) && !_queue.SequenceEqual(queue));
            _upcoming = upcoming;
            _queue = queue;
            if (changed)
            {
                _failed.RemoveWhere(file => !upcoming.Contains(file) && !queue.Contains(file));
            }
            _allowed = allowed;
            if (_running || lifetime.IsCancellationRequested || FindNext().File is null) return null;

            _running = true;
            return Task.Run(RunAsync);
        }
    }

    public void Finish()
    {
        lock (_gate) _running = false;
    }

    private (TrackFile? File, bool PreparePcm) FindNext()
    {
        if (!_allowed) return default;
        var upcoming = Array.Find(_upcoming, file => !_failed.Contains(file) && !isPrepared(file));
        if (upcoming is not null) return (upcoming, true);
        return isAnalyzed is null ? default :
            (Array.Find(_queue, file => !_failed.Contains(file) && !isAnalyzed(file)), false);
    }

    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            (TrackFile? File, bool PreparePcm) next;
            lock (_gate) next = FindNext();
            if (next.File is not { } file) return;

            try
            {
                var work = next.PreparePcm ? prepare(file) : analyze!(file);
                if (!work.IsCompleted) progress();
                await work.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A foreground selection can supersede a shared decode. The file can still be preloaded.
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_upcoming.Contains(file) || _queue.Contains(file)) _failed.Add(file);
                }
                System.Diagnostics.Debug.WriteLine($"LavPulse background analysis: {file.Name}: {ex}");
            }
            progress();
            try
            {
                // Leave time for playback and other work between decodes.
                await Task.Delay(100, lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
