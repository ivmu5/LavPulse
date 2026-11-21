using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class TrackLibraryPanel
{
    private static readonly string[] SizeUnits = ["Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ", "ЭБ"];
    private IReadOnlyDictionary<string, long?>? _folderSizes;
    private CancellationTokenSource? _sizeRead;

    private void ResetFolderSizes()
    {
        _sizeRead?.Cancel();
        _sizeRead?.Dispose();
        _sizeRead = null;
        _folderSizes = null;
    }

    private async Task ReadFolderSizesAsync(TrackFolderSource root, TrackFolder listing)
    {
        _sizeRead = CancellationTokenSource.CreateLinkedTokenSource(_token);
        var token = _sizeRead.Token;
        try
        {
            var sizes = await Task.Run(() => TrackFolderSizes.ReadAsync(root, listing, token), token);
            if (token.IsCancellationRequested) return;
            _folderSizes = sizes;
            foreach (var view in _tree.RealizedRows) view.UpdateSize();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private string SizeText(TreeRow row)
    {
        if (row.File is null && _folderSizes is null) return "…";
        long? bytes = row.File is { } file ? file.SizeBytes : _folderSizes?.GetValueOrDefault(row.Folders[^1].Id);
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
