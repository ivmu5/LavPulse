namespace Laveyana.Audio.Playback;

/// <summary>
/// Thread-safe ring of render-block deck states indexed by cumulative output frames. The
/// device's playback-head position picks the state actually reaching the output rather
/// than the state already decoded several blocks ahead. No audio-thread allocations.
/// </summary>
internal sealed class AudioPresentationHistory
{
    private const int Capacity = 512;
    private readonly (long Frame, QueueSnapshot Snapshot)[] _entries = new (long, QueueSnapshot)[Capacity];
    private readonly object _gate = new();
    private int _count = 1;
    private int _next = 1;
    private long _rendered;
    private long _played = long.MaxValue; // In-memory outputs without a device counter use rendered time.
    private long _delay;

    public AudioPresentationHistory(QueueSnapshot initial) => _entries[0] = (0, initial);

    public void SetDelay(long frames)
    {
        lock (_gate) _delay = Math.Max(0, frames);
    }

    public void ReportOutputPosition(long frames)
    {
        lock (_gate) _played = frames == long.MaxValue ? long.MaxValue : Math.Max(0, frames);
    }

    public void Append(int frames, QueueSnapshot snapshot)
    {
        if (frames <= 0) return;
        lock (_gate)
        {
            _rendered += frames;
            _entries[_next] = (_rendered, snapshot);
            _next = (_next + 1) % Capacity;
            _count = Math.Min(Capacity, _count + 1);
        }
    }

    public long PresentedFrame
    {
        get
        {
            lock (_gate) return PresentedFrameUnsafe();
        }
    }

    private long PresentedFrameUnsafe()
    {
        long output = _played == long.MaxValue ? _rendered : Math.Min(_played, _rendered);
        return Math.Max(0, output - _delay);
    }

    public QueueSnapshot Read()
    {
        lock (_gate)
        {
            long target = PresentedFrameUnsafe();
            int oldest = (_next + Capacity - _count) % Capacity;
            int newest = (_next + Capacity - 1) % Capacity;
            if (target >= _entries[newest].Frame) return _entries[newest].Snapshot;
            if (target <= _entries[oldest].Frame) return _entries[oldest].Snapshot;
            // Search the logical order of the ring even after it wraps.
            // Keep the render-side monitor held for at most log2(Capacity) probes.
            int low = 0, high = _count - 1;
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                if (_entries[(oldest + mid) % Capacity].Frame <= target) low = mid;
                else high = mid - 1;
            }
            return _entries[(oldest + low) % Capacity].Snapshot;
        }
    }
}
