namespace Laveyana.LavPulse.Tracks;

/// <summary>Bounds track scans and leaves a decoder slot available for PCM preparation.</summary>
internal sealed class TrackAnalysisSlots(int parallelism)
{
    private readonly object _gate = new();
    private int _parallelism = ValidateParallelism(parallelism);
    private int _scans;
    private int _decoders;
    private bool _helper;
    private TaskCompletionSource? _changed;

    private static int ValidateParallelism(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfEqual(value, int.MaxValue);
        return value;
    }

    public void SetParallelism(int parallelism)
    {
        ValidateParallelism(parallelism);
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

    // Optional second analysis lane; never wait for it or hold up a deck.
    // Keep it out of the reserved decoder slot accounting. At most one
    // helper can run per library, and crowded devices fall back to serial.
    public bool TryAcquireHelper(int processors)
    {
        lock (_gate)
        {
            if (_helper || processors < 2 || _decoders >= Math.Max(2, processors - 1))
                return false;
            _helper = true;
            return true;
        }
    }

    public void ReleaseHelper()
    {
        lock (_gate)
        {
            if (!_helper) throw new InvalidOperationException("No analysis helper was acquired.");
            _helper = false;
        }
    }

    public void Release(bool scan)
    {
        lock (_gate)
        {
            if (scan ? _scans == 0 : _decoders == _scans)
                throw new InvalidOperationException("No matching decoder slot was acquired.");
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
