using System.Diagnostics;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public enum TrackScanOutcome { Scanned, Skipped, Failed, Stopped }

public readonly record struct TrackScanResult(TrackFile File, TrackScanOutcome Outcome,
    long? BeforeBytes, long? AfterBytes, TimeSpan Elapsed);

public sealed class TrackScanBatch : IDisposable
{
    private readonly int _parallelism;
    private readonly CancellationTokenSource _stop;
    private readonly List<Scan> _running;

    public TrackScanBatch(int parallelism, CancellationToken lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        _parallelism = parallelism;
        _running = new List<Scan>(parallelism);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    public CancellationToken Token => _stop.Token;
    public void Skip(TrackFile file)
    {
        if (_running.Find(item => ReferenceEquals(item.File, file)) is { Work.IsCompleted: false } scan)
            scan.Cancellation.Cancel();
    }
    public void Stop() => _stop.Cancel();

    public async Task<(int Scanned, int Skipped, int Failed)> RunAsync(IReadOnlyList<TrackFile> files,
        Func<TrackFile, CancellationToken, Task<bool>> scan, Action<IReadOnlyList<TrackFile>, int, int> show,
        Action<TrackFile> completed,
        Func<TrackFile, CancellationToken, ValueTask<long?>>? measure = null,
        Action<TrackScanResult>? finishedRow = null)
    {
        int next = 0;
        int scanned = 0;
        int skipped = 0;
        int failed = 0;
        try
        {
            while (true)
            {
                while (_running.Count < _parallelism && next < files.Count && !Token.IsCancellationRequested)
                {
                    var file = files[next++];
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
                    _running.Add(new(file, cancellation, ScanAsync(file, scan, measure, cancellation.Token)));
                }
                if (!Token.IsCancellationRequested)
                    show(_running.Select(item => item.File).ToArray(),
                        scanned + skipped + failed, files.Count);
                if (_running.Count == 0) break;

                var work = await Task.WhenAny(_running.Select(item => item.Work));
                int index = _running.FindIndex(item => ReferenceEquals(item.Work, work));
                var finished = _running[index];
                _running.RemoveAt(index);
                finished.Cancellation.Dispose();
                var result = await work;
                switch (result.Outcome)
                {
                    case TrackScanOutcome.Scanned:
                        scanned++;
                        completed(finished.File);
                        break;
                    case TrackScanOutcome.Skipped: skipped++; break;
                    case TrackScanOutcome.Failed: failed++; break;
                }
                finishedRow?.Invoke(result);
            }
        }
        finally
        {
            if (_running.Count > 0)
            {
                Stop();
                await Task.WhenAll(_running.Select(item => item.Work));
                foreach (var item in _running) item.Cancellation.Dispose();
                _running.Clear();
            }
        }
        return (scanned, skipped, failed);
    }

    private async Task<TrackScanResult> ScanAsync(TrackFile file,
        Func<TrackFile, CancellationToken, Task<bool>> scan,
        Func<TrackFile, CancellationToken, ValueTask<long?>>? measure, CancellationToken token)
    {
        long? before = null, after = null;
        var watch = new Stopwatch();
        TrackScanOutcome outcome;
        try
        {
            token.ThrowIfCancellationRequested();
            before = await MeasureAsync(file, measure, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            watch.Start();
            outcome = await scan(file, token).ConfigureAwait(false)
                ? TrackScanOutcome.Scanned : TrackScanOutcome.Skipped;
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { outcome = TrackScanOutcome.Stopped; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { outcome = TrackScanOutcome.Skipped; }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            outcome = TrackScanOutcome.Failed;
        }
        finally { watch.Stop(); }

        // Measurements are best effort and never turn successful analysis into a failure.
        if (!token.IsCancellationRequested)
            after = await MeasureAsync(file, measure, token).ConfigureAwait(false);
        return new(file, outcome, before, after, watch.Elapsed);
    }

    private static async ValueTask<long?> MeasureAsync(TrackFile file,
        Func<TrackFile, CancellationToken, ValueTask<long?>>? measure, CancellationToken token)
    {
        if (measure is null) return null;
        try { return await measure(file, token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private sealed record Scan(TrackFile File, CancellationTokenSource Cancellation, Task<TrackScanResult> Work);

    public void Dispose() => _stop.Dispose();
}
