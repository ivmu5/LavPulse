using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>Retains recently loaded library thumbnails while rows are recycled.</summary>
internal sealed class TrackMetadataCache
{
    private const int MaximumEntries = 96;
    private const int ArtworkBudget = 4 * 1024 * 1024;
    private readonly OrderedDictionary<TrackFile, TrackMetadata> _entries = new(ReferenceEqualityComparer.Instance);
    private int _artworkBytes;

    public TrackMetadata? Get(TrackFile file) => _entries.GetValueOrDefault(file);

    public void Store(TrackFile file, TrackMetadata metadata)
    {
        if (_entries.Remove(file, out var previous)) _artworkBytes -= previous.Artwork.Length;
        int bytes = metadata.Artwork.Length;
        if (bytes > ArtworkBudget) return;

        while (_entries.Count >= MaximumEntries || _artworkBytes + bytes > ArtworkBudget)
            RemoveAt(0);
        _entries.Add(file, metadata);
        _artworkBytes += bytes;
    }

    public void Invalidate(IReadOnlySet<string> paths)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries.GetAt(i).Key.LocalPath is { } path && paths.Contains(path)) RemoveAt(i);
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _artworkBytes = 0;
    }

    private void RemoveAt(int index)
    {
        _artworkBytes -= _entries.GetAt(index).Value.Artwork.Length;
        _entries.RemoveAt(index);
    }
}
