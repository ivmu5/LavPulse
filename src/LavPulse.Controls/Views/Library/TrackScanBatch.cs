using System.Diagnostics;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

internal sealed class TrackScanBatch(CancellationToken lifetime) : IDisposable
{
    private const int ParallelTracks = 2;
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Scan? _preview;

    public CancellationToken Token => _stop.Token;
    public bool CanSkip => _preview is { Work.IsCompleted: false, Cancellation.IsCancellationRequested: false };
    public void Skip()
    {
        if (_preview is { Work.IsCompleted: false } scan) scan.Cancellation.Cancel();
    }
    public void Stop() => _stop.Cancel();

    public async Task<(int Scanned, int Skipped, int Failed)> RunAsync(IReadOnlyList<TrackFile> files,
        Func<TrackFile, CancellationToken, Task> scan, Action<TrackFile, int, int, int> show, Action<TrackFile> completed)
    {
        int next = 0;
        int scanned = 0;
        int skipped = 0;
        int failed = 0;
        var running = new List<Scan>(ParallelTracks);
        try
        {
            while (true)
            {
                while (running.Count < ParallelTracks && next < files.Count && !Token.IsCancellationRequested)
                {
                    var file = files[next++];
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
                    running.Add(new(file, cancellation, ScanAsync(file, scan, cancellation.Token)));
                }
                if (running.Count == 0) break;

                // Keep the oldest active scan in the preview. Skip applies only to that track.
                _preview = running.Find(item => !item.Work.IsCompleted);
                if (_preview is { } preview && !Token.IsCancellationRequested)
                    show(preview.File, scanned + skipped + failed, files.Count, running.Count(item => !item.Work.IsCompleted));

                var work = await Task.WhenAny(running.Select(item => item.Work));
                int index = running.FindIndex(item => ReferenceEquals(item.Work, work));
                var finished = running[index];
                running.RemoveAt(index);
                _preview = null;
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
            _preview = null;
            if (running.Count > 0)
            {
                Stop();
                await Task.WhenAll(running.Select(item => item.Work));
                foreach (var item in running) item.Cancellation.Dispose();
            }
        }
        return (scanned, skipped, failed);
    }

    private async Task<Outcome> ScanAsync(TrackFile file, Func<TrackFile, CancellationToken, Task> scan, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await scan(file, token).ConfigureAwait(false);
            return Outcome.Scanned;
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
