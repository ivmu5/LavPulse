using System.Diagnostics;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

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
                    if (!TrackFile.IsMp3(file.Name)) continue;
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
                    if (!first && pending < ImportBatchSize &&
                        Stopwatch.GetElapsedTime(lastCheckpoint) < ImportCheckpointInterval) continue;
                    await FlushAsync();
                    if (first && !_closed && _playback.Snapshot.Current is null && !IsLoadingFirst)
                        _ = LoadFirstAsync();
                }
            }
            finally
            {
                try { if (pending > 0) await FlushAsync(); }
                finally { _importGate.Release(); }
            }

            async Task FlushAsync()
            {
                pending = 0;
                lastCheckpoint = Stopwatch.GetTimestamp();
                var save = SaveQueueAsync();
                if (addToQueue && !_closed) UpdateQueue();
                await save;
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
