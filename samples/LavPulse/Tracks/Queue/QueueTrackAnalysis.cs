using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Prepares the current lookahead one track at a time, independently of the UI dispatcher.</summary>
internal sealed class QueueTrackAnalysis(Func<TrackFile, bool> isPrepared, Func<TrackFile, Task> prepare,
    Action progress, CancellationToken lifetime,
    Func<TrackFile, bool>? isAnalyzed = null, Func<TrackFile, Task>? analyze = null)
{
    private readonly object _gate = new();
    private readonly HashSet<TrackFile> _failed = [];
    private readonly Dictionary<TrackFile, int> _cancelled = [];
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
            bool changed = !_upcoming.SequenceEqual(upcoming) ||
                _pcmLookahead != pcmLookahead || _analysisLookahead != analysisLookahead;
            _upcoming = upcoming;
            _pcmLookahead = Math.Max(0, pcmLookahead);
            _analysisLookahead = Math.Max(0, analysisLookahead);
            if (changed)
            {
                _failed.RemoveWhere(file => !upcoming.Contains(file));
                foreach (var file in _cancelled.Keys.Where(file => !upcoming.Contains(file)).ToArray())
                    _cancelled.Remove(file);
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
        foreach (var file in _upcoming.Take(_pcmLookahead))
        {
            if (!_failed.Contains(file) && !isPrepared(file)) return (file, true);
        }
        // Scan the wider lookahead without materializing decoded audio.
        if (isAnalyzed is not null && analyze is not null)
            foreach (var file in _upcoming.Take(_analysisLookahead))
                if (!_failed.Contains(file) && !isPrepared(file) && !isAnalyzed(file))
                    return (file, false);
        return default;
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
                lock (_gate)
                {
                    _cancelled.Remove(file);
                    // A completed task must publish its result before returning.
                    // Otherwise the same queue item would spin forever.
                    if (_upcoming.Contains(file) && !(next.PreparePcm ? isPrepared(file) : isAnalyzed!(file)))
                        _failed.Add(file);
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
                        int count = _cancelled.GetValueOrDefault(file) + 1;
                        _cancelled[file] = count;
                        if (count >= 3) _failed.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_upcoming.Contains(file)) _failed.Add(file);
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
