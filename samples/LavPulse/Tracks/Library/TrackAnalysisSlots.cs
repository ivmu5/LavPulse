namespace Laveyana.LavPulse.Tracks;

/// <summary>Bounds track scans and leaves a decoder slot available for PCM preparation.</summary>
internal sealed class TrackAnalysisSlots(int parallelism)
{
    private readonly object _gate = new();
    private int _parallelism = parallelism;
    private int _scans;
    private int _decoders;
    private TaskCompletionSource? _changed;

    public void SetParallelism(int parallelism)
    {
        lock (_gate)
        {
            bool increased = parallelism > _parallelism;
            _parallelism = parallelism;
            if (increased) WakeWaiters();
        }
    }

    public async ValueTask AcquireAsync(bool scan, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_decoders <= _parallelism && (!scan || _scans < _parallelism))
                {
                    _decoders++;
                    if (scan) _scans++;
                    return;
                }
                changed = (_changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public void Release(bool scan)
    {
        lock (_gate)
        {
            if (scan) _scans--;
            _decoders--;
            WakeWaiters();
        }
    }

    private void WakeWaiters()
    {
        _changed?.TrySetResult();
        _changed = null;
    }
}
