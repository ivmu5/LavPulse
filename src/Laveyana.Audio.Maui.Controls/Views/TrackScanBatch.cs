using System.Diagnostics;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

internal sealed class TrackScanBatch(CancellationToken lifetime) : IDisposable
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private CancellationTokenSource? _current;

    public CancellationToken Token => _stop.Token;
    public void Skip() => _current?.Cancel();
    public void Stop() => _stop.Cancel();

    public async Task<(int Scanned, int Skipped, int Failed)> RunAsync(IReadOnlyList<TrackFile> files,
        Func<TrackFile, CancellationToken, Task> scan, Action<TrackFile, int, int> show, Action<TrackFile> completed)
    {
        int scanned = 0;
        int skipped = 0;
        int failed = 0;
        for (int index = 0; index < files.Count && !Token.IsCancellationRequested; index++)
        {
            var file = files[index];
            using var current = CancellationTokenSource.CreateLinkedTokenSource(Token);
            _current = current;
            try
            {
                show(file, index + 1, files.Count);
                current.Token.ThrowIfCancellationRequested();
                await scan(file, current.Token);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
                skipped++;
                continue;
            }
            catch (Exception ex)
            {
                failed++;
                Debug.WriteLine(ex);
                continue;
            }
            finally { _current = null; }
            scanned++;
            completed(file);
        }
        return (scanned, skipped, failed);
    }

    public void Dispose() => _stop.Dispose();
}
