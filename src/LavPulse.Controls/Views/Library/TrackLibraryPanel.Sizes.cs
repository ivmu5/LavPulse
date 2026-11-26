using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public sealed partial class TrackLibraryPanel
{
    private static readonly string[] SizeUnits = ["Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ", "ЭБ"];
    private readonly Func<TrackFile, CancellationToken, ValueTask<long?>>? _readLocalStorageSize;
    private IReadOnlyDictionary<string, long?>? _folderSizes;
    private IReadOnlyDictionary<string, long?>? _localTrackSizes;
    private HashSet<string> _changedStoragePaths = new(StringComparer.Ordinal);
    private CancellationTokenSource? _sizeRead;
    private bool _sizeRefreshQueued;

    private void ResetFolderSizes()
    {
        _sizeRead?.Cancel();
        _sizeRead = null;
        _folderSizes = null;
        _localTrackSizes = null;
        _changedStoragePaths.Clear();
    }

    private async Task ReadFolderSizesAsync(TrackFolderSource root, TrackFolder listing)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(_token);
        _sizeRead = read;
        var token = read.Token;
        var readStorage = _local ? _readLocalStorageSize : null;
        var previous = _localTrackSizes;
        var changed = _changedStoragePaths;
        _changedStoragePaths = new(StringComparer.Ordinal);
        var measured = new Dictionary<string, long?>(StringComparer.Ordinal);
        try
        {
            var sizes = await Task.Run(() => TrackFolderSizes.ReadAsync(root, listing, token,
                readStorage is null ? null : ReadLocalSizeAsync), token);
            if (token.IsCancellationRequested) return;
            _folderSizes = sizes;
            _localTrackSizes = readStorage is null ? null : measured;
            foreach (var view in _tree.RealizedRows) view.UpdateSize();
            foreach (var view in _activeScans.Values) view.UpdateSize();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_sizeRead, read))
            {
                _sizeRead = null;
                QueueStorageSizeRefresh();
            }
        }

        async ValueTask<long?> ReadLocalSizeAsync(TrackFile file, CancellationToken cancellation)
        {
            if (file.LocalPath is not { } path) return file.SizeBytes;
            if (measured.TryGetValue(path, out var bytes)) return bytes;
            if (changed.Contains(path) || previous?.TryGetValue(path, out bytes) != true)
                bytes = await readStorage!(file, cancellation).ConfigureAwait(false);
            return measured[path] = bytes;
        }
    }

    public void RefreshTrackStorageSize(string localPath)
    {
        if (_closed || !_local || _readLocalStorageSize is null ||
            (_localTrackSizes is not null && !_localTrackSizes.ContainsKey(localPath))) return;
        _changedStoragePaths.Add(localPath);
        QueueStorageSizeRefresh();
    }

    private void QueueStorageSizeRefresh()
    {
        if (_sizeRead is not null || _sizeRefreshQueued || _changedStoragePaths.Count == 0) return;
        _sizeRefreshQueued = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
        {
            _sizeRefreshQueued = false;
            if (_closed || !_local || _sizeRead is not null || _changedStoragePaths.Count == 0 || _rows.Count == 0)
                return;
            var root = _rows[0].Folders[0];
            if (_listings.TryGetValue(root.Id, out var listing)) _ = ReadFolderSizesAsync(root, listing);
        });
    }

    private string SizeText(TreeRow row)
    {
        long? bytes;
        if (row.File is not { } file)
        {
            if (_folderSizes is null) return "…";
            bytes = _folderSizes.GetValueOrDefault(row.Folders[^1].Id);
        }
        else if (_local && _readLocalStorageSize is not null && file.LocalPath is { } path)
        {
            if (_localTrackSizes is null) return "…";
            bytes = _localTrackSizes.GetValueOrDefault(path);
        }
        else bytes = file.SizeBytes;
        return FormatSize(bytes);
    }

    private static string FormatSize(long? bytes)
    {
        if (bytes is not >= 0) return "—";
        double value = bytes.Value;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#}\u00A0{SizeUnits[unit]}";
    }
}
