using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Prepares the current lookahead one track at a time, independently of the UI dispatcher.</summary>
internal sealed class QueueTrackAnalysis(Func<TrackFile, bool> isPrepared, Func<TrackFile, Task> prepare,
    Action progress, CancellationToken lifetime,
    Func<TrackFile, bool>? isAnalyzed = null, Func<TrackFile, Task>? analyze = null)
{
    private readonly object _gate = new();
    private readonly HashSet<(TrackFile File, bool PreparePcm)> _failed = [];
    private readonly Dictionary<(TrackFile File, bool PreparePcm), int> _cancelled = [];
    private TrackFile[] _upcoming = [];
    private int _pcmLookahead;
    private int _analysisLookahead;
    private bool _allowed;
    private bool _running;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public Task? Update(TrackFile[] upcoming, bool allowed, int pcmLookahead, int analysisLookahead)
    {
        lock (_gate)
        {
            pcmLookahead = Math.Max(0, pcmLookahead);
            analysisLookahead = Math.Max(0, analysisLookahead);
            bool changed = !_upcoming.SequenceEqual(upcoming) ||
                _pcmLookahead != pcmLookahead || _analysisLookahead != analysisLookahead;
            _upcoming = upcoming;
            _pcmLookahead = Math.Max(0, pcmLookahead);
            _analysisLookahead = Math.Max(0, analysisLookahead);
            if (changed)
            {
                _failed.RemoveWhere(key => !upcoming.Contains(key.File));
                foreach (var key in _cancelled.Keys.Where(key => !upcoming.Contains(key.File)).ToArray())
                    _cancelled.Remove(key);
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
        for (int i=0; i<Math.Min(_pcmLookahead,_upcoming.Length);i++)
        {
            var file=_upcoming[i];
            if (!_failed.Contains((file,true)) && !isPrepared(file)) return (file, true);
        }
        // A failed compact scan must not block a later playable preparation, or vice versa.
        if (isAnalyzed is not null && analyze is not null)
            for (int i=0;i<Math.Min(_analysisLookahead,_upcoming.Length);i++)
            {
                var file=_upcoming[i];
                if (!_failed.Contains((file,false)) && !isPrepared(file) && !isAnalyzed(file))
                    return (file, false);
            }
        return default;
    }

    private void NotifyProgress()
    {
        try { progress(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LavPulse analysis progress: {ex}"); }
    }

    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            (TrackFile? File, bool PreparePcm) next;
            lock (_gate) next = FindNext();
            if (next.File is not { } file) return;
            var key = (File: file, PreparePcm: next.PreparePcm);

            try
            {
                var work = next.PreparePcm ? prepare(file) : analyze!(file);
                if (!work.IsCompleted) NotifyProgress();
                await work.ConfigureAwait(false);
                lock (_gate)
                {
                    _cancelled.Remove(key);
                    // A completed task must publish its result before returning.
                    // Otherwise the same queue item would spin forever.
                    if (_upcoming.Take(next.PreparePcm ? _pcmLookahead : _analysisLookahead).Contains(file) &&
                        !(next.PreparePcm ? isPrepared(file) : isAnalyzed!(file)))
                        _failed.Add(key);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Retry a superseded foreground decode, but do not spin
                // indefinitely on an independently cancelled provider.
                lock (_gate)
                {
                    if (_upcoming.Contains(file))
                    {
                        int count = _cancelled.GetValueOrDefault(key) + 1;
                        _cancelled[key] = count;
                        if (count >= 3) _failed.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_upcoming.Contains(file)) _failed.Add(key);
                }
                System.Diagnostics.Debug.WriteLine($"LavPulse background analysis: {file.Name}: {ex}");
            }
            NotifyProgress();
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
