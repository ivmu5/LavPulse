using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

internal sealed partial class PlayerLibrary
{
    /// <summary>Evicts older entries. The occupied decks and upcoming tracks remain cached for playback.</summary>
    public void Trim(CachedTrack? current, CachedTrack? incoming, TrackFile? selected, TrackFile? armed,
        IReadOnlyCollection<TrackFile> upcoming)
    {
        lock (_cacheGate) TrimCache(current, incoming, selected, armed, upcoming);
    }

    private void TrimCache(CachedTrack? current, CachedTrack? incoming, TrackFile? selected, TrackFile? armed,
        IReadOnlyCollection<TrackFile> upcoming)
    {
        long totalBytes = 0;
        long coverBytes = 0;
        foreach (var entry in _prepared.Values)
        {
            var task = entry.Work;
            if (!task.IsCompletedSuccessfully) continue;
            totalBytes += task.Result.PcmSizeBytes;
            coverBytes += task.Result.Artwork.Length;
        }
        if (totalBytes > LocalTrackStore.PcmBudgetBytes || coverBytes > ArtworkBudgetBytes)
        {
            var evicted = new List<(TrackFile File, CachedTrack Track)>();
            foreach (var file in _pcmAccess.OrderBy(entry => entry.Value).Select(entry => entry.Key).ToArray())
            {
                if (totalBytes <= LocalTrackStore.PcmBudgetBytes && coverBytes <= ArtworkBudgetBytes) break;
                if (file == selected || file == armed || upcoming.Contains(file) || GetPrepared(file) is not { } track ||
                    ReferenceEquals(track, current) || ReferenceEquals(track, incoming)) continue;

                _prepared.Remove(file);
                _pcmAccess.Remove(file);
                _decodedFiles.TryRemove(track, out _);
                totalBytes -= track.PcmSizeBytes;
                coverBytes -= track.Artwork.Length;
                evicted.Add((file, track));
            }
            if (evicted.Count != 0) _cacheCleanup = DeleteAsync(_cacheCleanup, evicted);
        }

        TrimArtwork(_metadata, _tagAccess, ArtworkBudgetBytes);
        TrimArtwork(_queueMetadata, _thumbnailAccess, ThumbnailBudgetBytes);
    }

    private static void TrimArtwork(Dictionary<TrackFile, Task<TrackMetadata>> metadata,
        Dictionary<TrackFile, long> access, long budget)
    {
        long total = metadata.Values.Where(task => task.IsCompletedSuccessfully).Sum(task => (long)task.Result.Artwork.Length);
        if (total <= budget) return;

        foreach (var file in metadata.Keys.OrderBy(file => access.GetValueOrDefault(file)).ToArray())
        {
            if (total <= budget) break;
            var tags = metadata[file];
            if (!tags.IsCompletedSuccessfully || tags.Result.Artwork.IsEmpty) continue;

            total -= tags.Result.Artwork.Length;
            metadata.Remove(file);
            access.Remove(file);
        }
    }

    private Task DeleteAsync(Task previous, List<(TrackFile File, CachedTrack Track)> tracks) => Task.Run(async () =>
    {
        await previous.ConfigureAwait(false);
        foreach (var (file, track) in tracks) store.EvictPcm(file, track);
    });
}
