using System.Diagnostics;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

internal sealed class TrackScanBatch(int parallelism, CancellationToken lifetime) : IDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly List<Scan> _running = new(parallelism);

    public CancellationToken Token => _stop.Token;
    public void Skip(TrackFile file)
    {
        if (_running.Find(item => ReferenceEquals(item.File, file)) is { Work.IsCompleted: false } scan)
            scan.Cancellation.Cancel();
    }
    public void Stop() => _stop.Cancel();

    public async Task<(int Scanned, int Skipped, int Failed)> RunAsync(IReadOnlyList<TrackFile> files,
        Func<TrackFile, CancellationToken, Task<bool>> scan, Action<IReadOnlyList<TrackFile>, int, int> show,
        Action<TrackFile> completed)
    {
        int next = 0;
        int scanned = 0;
        int skipped = 0;
        int failed = 0;
        try
        {
            while (true)
            {
                while (_running.Count < parallelism && next < files.Count && !Token.IsCancellationRequested)
                {
                    var file = files[next++];
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
                    _running.Add(new(file, cancellation, ScanAsync(file, scan, cancellation.Token)));
                }
                if (!Token.IsCancellationRequested)
                    show(_running.Where(item => !item.Work.IsCompleted).Select(item => item.File).ToArray(),
                        scanned + skipped + failed, files.Count);
                if (_running.Count == 0) break;

                var work = await Task.WhenAny(_running.Select(item => item.Work));
                int index = _running.FindIndex(item => ReferenceEquals(item.Work, work));
                var finished = _running[index];
                _running.RemoveAt(index);
                finished.Cancellation.Dispose();
                switch (await work)
                {
                    case Outcome.Scanned:
                        scanned++;
                        completed(finished.File);
                        break;
                    case Outcome.Skipped: skipped++; break;
                    case Outcome.Failed: failed++; break;
                }
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

    private async Task<Outcome> ScanAsync(TrackFile file, Func<TrackFile, CancellationToken, Task<bool>> scan, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            return await scan(file, token).ConfigureAwait(false) ? Outcome.Scanned : Outcome.Skipped;
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { return Outcome.Stopped; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Outcome.Skipped; }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return Outcome.Failed;
        }
    }

    private sealed record Scan(TrackFile File, CancellationTokenSource Cancellation, Task<Outcome> Work);
    private enum Outcome { Scanned, Skipped, Failed, Stopped }

    public void Dispose() => _stop.Dispose();
}
