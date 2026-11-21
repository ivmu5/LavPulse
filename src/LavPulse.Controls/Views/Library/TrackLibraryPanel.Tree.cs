using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public sealed partial class TrackLibraryPanel
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackFolder> _listings = new(StringComparer.Ordinal);
    private readonly TrackMetadataCache _metadataCache = new();
    private TreeRow? _selected;

    private async Task OpenLibraryAsync() => await ShowRootAsync(await _openLibrary(), local: true);

    private async Task PickAsync()
    {
        if (await _pickFolder() is not { } root) return;
        await ShowRootAsync(root, local: false);
        _token.ThrowIfCancellationRequested();
        ImportFolderOpened?.Invoke();
    }

    private async Task ShowRootAsync(TrackFolderSource root, bool local, bool preservePosition = false)
    {
        var listing = await root.ReadAsync(_token);
        _token.ThrowIfCancellationRequested();
        var previous = preservePosition ? _selected : null;
        _listings.Clear();
        _listings.Add(root.Id, listing);
        if (preservePosition)
        {
            if (_expanded.Contains(root.Id)) await ReadExpandedAsync(listing);
        }
        else
        {
            _expanded.Clear();
            _expanded.Add(root.Id);
            _editing = false;
            _scrollOffset = 0;
            _tree.SetEndPadding(0);
        }

        _token.ThrowIfCancellationRequested();
        ResetFolderSizes();
        _metadataCache.Clear();
        _rows.Clear();
        _local = local;
        var row = new TreeRow([root], null) { Expanded = _expanded.Contains(root.Id) };
        _rows.Add(row);
        if (row.Expanded) _rows.AddRange(ExpandedRows(row, listing));
        _selected = RestoreSelection(previous) ?? row;
        _selected.Selected = true;
        if (preservePosition) KeepScrollOffset(_rows.Count);
        _tree.SetItems(_rows);
        _ = ReadFolderSizesAsync(root, listing);
        _heading.Text = local ? "Медиатека" : "Импорт папки";
        _message.Text = listing is { Files.Count: 0, Children.Count: 0 } ? "Здесь пока нет MP3." : "";
        if (!preservePosition && _tree.ScrollY > 0) await _tree.ScrollToAsync(0, 0, animated: false);
    }

    private async Task ReadExpandedAsync(TrackFolder listing)
    {
        foreach (var child in listing.Children)
        {
            if (!_expanded.Contains(child.Id) || _listings.ContainsKey(child.Id)) continue;
            var contents = await child.ReadAsync(_token);
            _listings.Add(child.Id, contents);
            await ReadExpandedAsync(contents);
        }
    }

    private TreeRow? RestoreSelection(TreeRow? previous)
    {
        if (previous is null) return null;
        var match = _rows.Find(row => row.Folders[^1].Id == previous.Folders[^1].Id &&
            row.File?.LocalPath == previous.File?.LocalPath && row.File?.Name == previous.File?.Name);
        return match ?? _rows.FindLast(row => row.File is null &&
            previous.Folders.Any(folder => folder.Id == row.Folders[^1].Id));
    }

    private async Task SelectAsync(TreeRow row)
    {
        if (row.File is null)
        {
            var folder = row.Folders[^1];
            int index = _rows.IndexOf(row);
            if (index < 0) return;
            if (row.Expanded)
            {
                int end = index + 1;
                while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
                KeepScrollOffset(_rows.Count - (end - index - 1));
                _rows.RemoveRange(index + 1, end - index - 1);
                _expanded.Remove(folder.Id);
            }
            else
            {
                if (!_listings.TryGetValue(folder.Id, out var listing))
                    _listings.Add(folder.Id, listing = await folder.ReadAsync(_token));
                _token.ThrowIfCancellationRequested();
                _expanded.Add(folder.Id);
                _rows.InsertRange(index + 1, ExpandedRows(row, listing));
                if (_tree.EndPadding > 0) KeepScrollOffset(_rows.Count);
            }
            row.Expanded = !row.Expanded;
        }
        if (_selected is { } previous && previous != row)
        {
            previous.Selected = false;
            previous.Refresh();
        }
        _selected = row;
        row.Selected = true;
        row.Refresh();
        if (row.File is null) _tree.SetItems(_rows);
        _message.Text = "";
    }

    private void KeepScrollOffset(int remainingRows)
    {
        // Keep the collapsed folder in place even if there are too few rows left below it.
        _tree.SetEndPadding(TrackListLayout.PaddingToKeepOffset(remainingRows, _scrollOffset, _tree.Height));
    }

    private IEnumerable<TreeRow> ExpandedRows(TreeRow row, TrackFolder listing)
    {
        foreach (var child in listing.Children)
        {
            if (row.Folders.Any(parent => parent.Id == child.Id)) continue;
            var visible = new TreeRow([.. row.Folders, child], null) { Expanded = _expanded.Contains(child.Id) };
            yield return visible;
            if (visible.Expanded && _listings.TryGetValue(child.Id, out var contents))
            {
                foreach (var descendant in ExpandedRows(visible, contents)) yield return descendant;
            }
        }
        foreach (var file in listing.Files)
            yield return new TreeRow(row.Folders, file);
    }

    private TrackLibrarySelection SelectionFor(TreeRow row) => new(row.Folders, row.File, _local);

    private sealed class TreeRow(TrackFolderSource[] folders, TrackFile? file)
    {
        public TrackFolderSource[] Folders { get; } = folders;
        public TrackFile? File { get; } = file;
        public int Depth => Folders.Length - (File is null ? 1 : 0);
        public bool Expanded { get; set; }
        public bool Selected { get; set; }
        public event Action? Changed;
        public event Action? MetadataChanged;
        public void Refresh() => Changed?.Invoke();
        public void RefreshMetadata() => MetadataChanged?.Invoke();
    }
}
