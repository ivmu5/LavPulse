using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

internal sealed partial class PlayerLibrary
{
    /// <summary>Evicts completed PCM outside the configured lookahead; occupied and selected decks stay pinned.</summary>
    public void Trim(CachedTrack? current, CachedTrack? incoming, TrackFile? selected, TrackFile? armed,
        IReadOnlyCollection<TrackFile> upcoming, CachedTrack? presentedCurrent = null,
        CachedTrack? presentedIncoming = null, CachedTrack? shownCurrent = null, CachedTrack? shownIncoming = null)
    {
        lock (_cacheGate)
        {
            if (_closing) return;
            TrimCache(current, incoming, selected, armed, upcoming,
                presentedCurrent, presentedIncoming, shownCurrent, shownIncoming);
        }
    }

    private void TrimCache(CachedTrack? current, CachedTrack? incoming, TrackFile? selected, TrackFile? armed,
        IReadOnlyCollection<TrackFile> upcoming, CachedTrack? presentedCurrent, CachedTrack? presentedIncoming,
        CachedTrack? shownCurrent, CachedTrack? shownIncoming)
    {
        // Failed/cancelled preparations do not consume PCM bytes, so a byte-only
        // budget would retain their exception graphs and tasks forever.
        foreach (var file in _prepared.Where(entry => entry.Value.Work.IsFaulted || entry.Value.Work.IsCanceled)
                     .Select(entry => entry.Key).ToArray())
        {
            if (file == selected || file == armed || upcoming.Contains(file)) continue;
            _prepared.Remove(file);
        }
        // The lookahead is a retention policy, not just a preparation hint.
        // Remove completed PCM outside it even below the former byte budget.
        // Pending work is allowed to finish; its completion triggers another trim.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // Pending aliases might already be opening a manifest referring to this PCM.
        // Defer eviction until their work has published a result.
        var pendingPaths = new HashSet<string>(comparer);
        foreach (var (file, entry) in _prepared)
            if (!entry.Work.IsCompleted && file.LocalPath is { } path) pendingPaths.Add(path);
        var evicted = new List<(TrackFile File, string Pcm)>();
        foreach (var (file, entry) in _prepared.ToArray())
        {
            if (!entry.Work.IsCompletedSuccessfully || file == selected || file == armed ||
                upcoming.Contains(file)) continue;
            if (file.LocalPath is { } source && pendingPaths.Contains(source)) continue;
            var track = entry.Work.Result;
            if (ReferenceEquals(track, current) || ReferenceEquals(track, incoming) ||
                ReferenceEquals(track, presentedCurrent) || ReferenceEquals(track, presentedIncoming) ||
                ReferenceEquals(track, shownCurrent) || ReferenceEquals(track, shownIncoming)) continue;

            _prepared.Remove(file);
            _decodedFiles.TryRemove(track, out _);
            evicted.Add((file, track.Path));
        }
        var retained = upcoming.Concat(_prepared.Keys).ToList();
        if (selected is not null) retained.Add(selected);
        if (armed is not null) retained.Add(armed);
        evicted.AddRange(store.TakeUnclaimedPcm(retained));
        if (evicted.Count != 0)
        {
            var retainedPcm = new HashSet<string>(comparer);
            foreach (var entry in _prepared.Values)
                if (entry.Work.IsCompletedSuccessfully) retainedPcm.Add(entry.Work.Result.Path);
            foreach (var track in new[] { current, incoming, presentedCurrent, presentedIncoming, shownCurrent, shownIncoming })
                if (track is not null) retainedPcm.Add(track.Path);
            // Retired aliases can refer to exactly the same decoded file.
            // Never delete a retained path, and delete each obsolete path once.
            evicted.RemoveAll(entry => !retainedPcm.Add(entry.Pcm));
            if (evicted.Count != 0) _cacheCleanup = DeleteAsync(_cacheCleanup, evicted);
        }

        TrimArtwork(_metadata, _tagAccess, ArtworkBudgetBytes);
        TrimArtwork(_queueMetadata, _thumbnailAccess, ThumbnailBudgetBytes);
    }

    private static void TrimArtwork(Dictionary<TrackFile, Task<TrackMetadata>> metadata,
        Dictionary<TrackFile, long> access, long budget)
    {
        // Queue rows are virtualized. Keep a generous recent-entry limit even when
        // tracks have no artwork, without evicting in-flight requests.
        const int entryBudget = 1024;
        long total = 0;
        foreach (var task in metadata.Values)
            if (task.IsCompletedSuccessfully) total += task.Result.Artwork.Length;
        if (total <= budget && metadata.Count <= entryBudget) return;

        foreach (var file in metadata.Keys.OrderBy(file => access.GetValueOrDefault(file)).ToArray())
        {
            if (total <= budget && metadata.Count <= entryBudget) break;
            var tags = metadata[file];
            if (!tags.IsCompleted) continue;
            long bytes = tags.IsCompletedSuccessfully ? tags.Result.Artwork.Length : 0;
            // A text-only entry cannot help satisfy the artwork byte budget.
            if (metadata.Count <= entryBudget && bytes == 0) continue;
            total -= bytes;
            metadata.Remove(file);
            access.Remove(file);
        }
    }

    private Task DeleteAsync(Task previous, List<(TrackFile File, string Pcm)> tracks) => Task.Run(async () =>
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        foreach (var (file, pcm) in tracks)
        {
            try { store.EvictPcm(file, pcm); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    });
}
