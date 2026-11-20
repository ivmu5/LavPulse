using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Prepares the current lookahead one track at a time, independently of the UI dispatcher.</summary>
internal sealed class QueueTrackAnalysis(Func<TrackFile, bool> isPrepared, Func<TrackFile, Task> prepare,
    Action progress, CancellationToken lifetime)
{
    private readonly object _gate = new();
    private readonly HashSet<TrackFile> _failed = [];
    private TrackFile[] _upcoming = [];
    private bool _allowed;
    private bool _running;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public Task? Update(TrackFile[] upcoming, bool allowed)
    {
        lock (_gate)
        {
            if (!_upcoming.SequenceEqual(upcoming))
            {
                _upcoming = upcoming;
                _failed.IntersectWith(upcoming);
            }
            _allowed = allowed;
            if (_running || lifetime.IsCancellationRequested || FindNext() is null) return null;

            _running = true;
            return Task.Run(RunAsync);
        }
    }

    public void Finish()
    {
        lock (_gate) _running = false;
    }

    private TrackFile? FindNext() => _allowed
        ? Array.Find(_upcoming, file => !_failed.Contains(file) && !isPrepared(file))
        : null;

    private async Task RunAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TrackFile? file;
            lock (_gate) file = FindNext();
            if (file is null) return;

            try
            {
                var work = prepare(file);
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
