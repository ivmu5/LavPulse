using System.Diagnostics;
using Laveyana.LavPulse.Tracks;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private const int ImportBatchSize = 16;
    private static readonly TimeSpan ImportCheckpointInterval = TimeSpan.FromMilliseconds(500);

    private TrackFile[] _analysisQueue = [];
    private readonly SemaphoreSlim _importGate = new(1);

    private Task<(IReadOnlyList<TrackFile> Tracks, int Updated)> ImportLocalTracksAsync(IReadOnlyList<TrackFile> files, bool loadFirst = false) =>
        ImportLocalTracksAsync(files.Select(file => new FolderTrack(file, [])).ToArray(), loadFirst);

    private async Task<(IReadOnlyList<TrackFile> Tracks, int Updated)> ImportLocalTracksAsync(IReadOnlyList<FolderTrack> files, bool loadFirst,
        bool alreadyLocal = false, bool addToQueue = true)
    {
        if (_closed) return ([], 0);
        var token = _lifetime.Token;
        var requested = new List<TrackFile>();
        int updated = 0;
        try
        {
            await (_restoreQueue ??= RestoreLocalQueueAsync());
            await _importGate.WaitAsync(token);
            int pending = 0;
            long lastCheckpoint = Stopwatch.GetTimestamp();
            try
            {
                foreach (var item in files)
                {
                    token.ThrowIfCancellationRequested();
                    var file = item.File;
                    if (!TrackFile.IsSupportedAudio(file.Name)) continue;
                    TrackFile local;
                    try { local = alreadyLocal ? file : await Task.Run(() => _trackStore.CopyAsync(file, token), token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _view.Status.Text = $"Не удалось сохранить {file.Name}. {ex.Message}";
                        continue;
                    }
                    if (_closed) break;
                    var canonical = _library.Add([local], [])[0];
                    bool remember = !alreadyLocal && _trackStore.Remember(local, item.Folders);
                    bool add = addToQueue && !_queue.Contains(canonical);
                    if (add)
                    {
                        _queue.AddRange([canonical]);
                    }
                    requested.Add(canonical);
                    if (!add && !remember) continue;
                    pending++;
                    if (addToQueue ? add : remember) updated++;
                    bool first = add && loadFirst && _playback.Snapshot.Current is null && !IsLoadingFirst;
                    if (alreadyLocal || (!first && pending < ImportBatchSize &&
                        Stopwatch.GetElapsedTime(lastCheckpoint) < ImportCheckpointInterval)) continue;
                    await FlushAsync();
                }
            }
            finally
            {
                try
                {
                    if (pending > 0)
                    {
                        var save = FlushAsync();
                        if (!alreadyLocal) await save;
                    }
                }
                finally { _importGate.Release(); }
            }

            Task FlushAsync()
            {
                pending = 0;
                lastCheckpoint = Stopwatch.GetTimestamp();
                var save = SaveQueueAsync();
                if (addToQueue && !_closed)
                {
                    if (loadFirst && _queue.Tracks.Count > 0 && _playback.Snapshot.Current is null && !IsLoadingFirst)
                        _ = LoadFirstAsync();
                    UpdateQueue();
                }
                return save;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        return (requested, updated);
    }

    /// <summary>Keeps each local copy and starts analysis as the files appear in the queue.</summary>
    private async Task AddFilesAsync(IReadOnlyList<TrackFile> files)
    {
        await ImportLocalTracksAsync(files, loadFirst: true);
        if (!_closed && !IsLoadingFirst) await SelectDefaultAsync();
    }
}
