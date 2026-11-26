using System.Buffers;
using System.Collections.Concurrent;

namespace Laveyana.Audio.Playback;

/// <summary>Single producer and single dedicated consumer. The decoder and
/// recording stream remain owned by the producer. Blocks are copied before
/// transfer, bounded, ordered, and returned exactly once.</summary>
internal sealed class ParallelKeyAnalysis : IDisposable
{
    internal const int Capacity = 4;
    private readonly BlockingCollection<Block> _pending = new(Capacity);
    private readonly CancellationTokenSource _stop;
    private readonly Task<TrackMusicalKey?> _worker;
    private int _disposed;
    private readonly MusicalKeyAnalyzer? _analyzer;
    private readonly TrackLoudnessAnalyzer? _loudness;
    internal TrackLoudness? Loudness => _loudness?.Finish();
    // Only accessed after Join, when the consumer no longer touches its state.
    internal TrackKeySection[] LocalKeys => _analyzer?.FinishLocal() ?? [];
    private readonly record struct Block(float[] Samples, int Count);
    public CancellationToken Token { get; }

    public ParallelKeyAnalysis(int sampleRate, CancellationToken token)
        : this(new MusicalKeyAnalyzer(sampleRate),
            sampleRate == PlaybackFormat.SampleRate ? new TrackLoudnessAnalyzer() : null, token) { }

    private ParallelKeyAnalysis(MusicalKeyAnalyzer analyzer, TrackLoudnessAnalyzer? loudness, CancellationToken token)
        : this((samples, count, cancellation) =>
        {
            var block = samples.AsSpan(0, count);
            loudness?.Add(block);
            cancellation.ThrowIfCancellationRequested();
            analyzer.Add(block, cancellation);
        }, analyzer.Finish, token)
    {
        _analyzer = analyzer;
        _loudness = loudness;
    }

    // Separate consumer seam for deterministic cancellation and fault tests.
    internal ParallelKeyAnalysis(Action<float[], int, CancellationToken> consume,
        Func<TrackMusicalKey?> finish, CancellationToken token)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Token = _stop.Token;
        try
        {
            // A dedicated worker cannot be starved by producers waiting for
            // bounded capacity in the thread pool.
            _worker = Task.Factory.StartNew(() => Consume(consume, finish),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            _pending.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public void Submit(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Token.ThrowIfCancellationRequested();
        if (samples.IsEmpty) return;
        var rented = ArrayPool<float>.Shared.Rent(samples.Length);
        bool transferred = false;
        try
        {
            samples.CopyTo(rented);
            _pending.Add(new(rented, samples.Length), Token);
            transferred = true;
        }
        finally
        {
            if (!transferred) ArrayPool<float>.Shared.Return(rented);
        }
    }

    public void CompleteInput()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _pending.CompleteAdding();
    }
    public TrackMusicalKey? Join() => _worker.GetAwaiter().GetResult();

    private TrackMusicalKey? Consume(Action<float[], int, CancellationToken> consume,
        Func<TrackMusicalKey?> finish)
    {
        try
        {
            foreach (var block in _pending.GetConsumingEnumerable(Token))
            {
                try { consume(block.Samples, block.Count, Token); }
                finally { ArrayPool<float>.Shared.Return(block.Samples); }
            }
            Token.ThrowIfCancellationRequested();
            return finish();
        }
        catch
        {
            // Unblock a producer waiting for a free slot even if the
            // consumer threw. The original exception is observed by Join.
            _stop.Cancel();
            throw;
        }
    }

    public void Dispose()
    {
        // The producer owns disposal and must stop submitting first.
        // A repeated cleanup must not return pooled blocks a second time.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_worker.IsCompleted) _stop.Cancel();
            _pending.CompleteAdding();
            try { Join(); }
            catch (Exception) { /* Preserve the producer or Join exception. */ }
        }
        finally
        {
            while (_pending.TryTake(out var block)) ArrayPool<float>.Shared.Return(block.Samples);
            _pending.Dispose();
            _stop.Dispose();
        }
    }
}
