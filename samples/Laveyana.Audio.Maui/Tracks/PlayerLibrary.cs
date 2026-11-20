using Laveyana.Audio.Playback;
using System.Collections.Concurrent;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Holds the window's file selection and deduplicates background preparation requests.</summary>
internal sealed class PlayerLibrary(TrackPreparation preparation, CancellationToken lifetime)
{
    private const long PcmBudgetBytes = 256L * 1024 * 1024;
    private const long ArtworkBudgetBytes = 32L * 1024 * 1024;
    private const long ThumbnailBudgetBytes = 24L * 1024 * 1024;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, TrackFile> _paths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<TrackFile> _files = [];
    // Populated by decoder continuations and read/evicted by the UI thread.
    private readonly ConcurrentDictionary<CachedTrack, TrackFile> _decodedFiles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TrackFile, long> _pcmAccess = new();
    private readonly Dictionary<TrackFile, long> _tagAccess = new();
    private readonly Dictionary<TrackFile, long> _thumbnailAccess = new();
    private long _access;
    private Task _cacheCleanup = Task.CompletedTask;
    private readonly Dictionary<TrackFile, Preparation> _prepared = new();
    private readonly Dictionary<TrackFile, Task<TrackMetadata>> _metadata = new();
    private readonly Dictionary<TrackFile, Task<TrackMetadata>> _queueMetadata = new();
    private readonly SemaphoreSlim _thumbnailSlots = new(2);
    private readonly SemaphoreSlim _decodeSlots = new(2);
    private readonly SemaphoreSlim _metadataSlots = new(3);

    /// <summary>Adds playable files while retaining stable identities for the queue and cached tracks.</summary>
    public IReadOnlyList<TrackFile> Add(IEnumerable<TrackFile> files, IReadOnlyList<TrackFile> queue)
    {
        var added = new List<TrackFile>();
        var included = new HashSet<TrackFile>(queue);
        foreach (var file in files)
        {
            if (!TrackFile.IsMp3(file.Name)) continue;

            TrackFile canonical;
            if (file.LocalPath is { } path)
            {
                if (!_paths.TryGetValue(path, out var existing)) _paths.Add(path, existing = file);
                canonical = existing;
            }
            else
            {
                if (!_files.TryGetValue(file, out var existing)) _files.Add(existing = file);
                canonical = existing;
            }

            if (included.Add(canonical)) added.Add(canonical);
        }
        return added;
    }

    /// <summary>Prepares a track once. A failed import can be retried through the library.</summary>
    public Task<CachedTrack> PrepareAsync(TrackFile file) => PrepareAsync(file, default);

    public Task<CachedTrack> PrepareAsync(TrackFile file, CancellationToken selection)
    {
        lock (_cacheGate)
        {
            selection.ThrowIfCancellationRequested();
            _pcmAccess[file] = ++_access;
            if (!_prepared.TryGetValue(file, out var pending) ||
                (!pending.Work.IsCompletedSuccessfully &&
                 (pending.Token.IsCancellationRequested || pending.Work.IsFaulted || pending.Work.IsCanceled)))
            {
                var token = selection.CanBeCanceled ? selection : lifetime;
                var metadata = _metadata.GetValueOrDefault(file);
                // Interactive requests can decode tags themselves instead of waiting behind thumbnails.
                if (selection.CanBeCanceled && metadata?.IsCompletedSuccessfully != true)
                    metadata = null;
                pending = new(DecodeAsync(file, metadata, token, pending?.Work), token);
                _prepared[file] = pending;
            }
            return selection.CanBeCanceled ? pending.Work.WaitAsync(selection) : pending.Work;
        }
    }

    private sealed record Preparation(Task<CachedTrack> Work, CancellationToken Token);

    public CachedTrack? GetPrepared(TrackFile file, bool forPlayback = false)
    {
        lock (_cacheGate)
        {
            if (!_prepared.TryGetValue(file, out var pending) || !pending.Work.IsCompletedSuccessfully) return null;
            if (forPlayback) _pcmAccess[file] = ++_access;
            return pending.Work.Result;
        }
    }

    public TrackFile? Find(CachedTrack track)
    {
        lock (_cacheGate)
        {
            if (!_decodedFiles.TryGetValue(track, out var file)) return null;
            _pcmAccess[file] = ++_access;
            return file;
        }
    }

    public bool HasQueueMetadataRequest(TrackFile file) => _queueMetadata.ContainsKey(file);

    public Task<TrackMetadata> ReadQueueMetadataAsync(TrackFile file)
    {
        if (!_queueMetadata.TryGetValue(file, out var task))
        {
            _queueMetadata[file] = task = BuildQueueMetadataAsync(ReadMetadataAsync(file));
        }
        return task;
    }

    private async Task<TrackMetadata> BuildQueueMetadataAsync(Task<TrackMetadata> metadata)
    {
        var info = await metadata.ConfigureAwait(false);
        if (info.Artwork.IsEmpty) return info;

        await _thumbnailSlots.WaitAsync(lifetime).ConfigureAwait(false);
        try
        {
            var thumbnail = await ArtworkThumbnail.CreateAsync(info.Artwork, lifetime).ConfigureAwait(false);
            return info with { Artwork = thumbnail };
        }
        finally
        {
            _thumbnailSlots.Release();
        }
    }

    /// <summary>Reads queue tags in parallel without making playback wait for the rest of a folder.</summary>
    public Task<TrackMetadata> ReadMetadataAsync(TrackFile file)
    {
        lock (_cacheGate)
        {
            _tagAccess[file] = ++_access;
            if (!_metadata.TryGetValue(file, out var task) || task.IsFaulted || task.IsCanceled)
            {
                _metadata[file] = task = _prepared.TryGetValue(file, out var pending) && !pending.Work.IsFaulted && !pending.Work.IsCanceled
                    ? ReadPreparedMetadataAsync(file, pending.Work)
                    : ReadTagsAsync(file);
            }
            return task;
        }
    }

    private async Task<TrackMetadata> ReadPreparedMetadataAsync(TrackFile file, Task<CachedTrack> pending)
    {
        try
        {
            var track = await pending.ConfigureAwait(false);
            return new(track.Title, track.Artist, track.Artwork);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            return await ReadTagsAsync(file).ConfigureAwait(false);
        }
    }

    public TrackMetadata? GetMetadata(TrackFile file)
    {
        lock (_cacheGate)
        {
            if (!_queueMetadata.TryGetValue(file, out var task) || !task.IsCompletedSuccessfully) return null;
            _thumbnailAccess[file] = ++_access;
            return task.Result;
        }
    }

    public bool IsPreparing(TrackFile file)
    {
        lock (_cacheGate)
            return _prepared.TryGetValue(file, out var pending) && !pending.Token.IsCancellationRequested && !pending.Work.IsCompleted;
    }

    private async Task<TrackMetadata> ReadTagsAsync(TrackFile file)
    {
        await _metadataSlots.WaitAsync(lifetime);
        try
        {
            return await preparation.ReadMetadataAsync(file, lifetime);
        }
        finally
        {
            _metadataSlots.Release();
        }
    }

    /// <summary>Limits simultaneous MP3 analysis on mobile while letting playback continue.</summary>
    private async Task<CachedTrack> DecodeAsync(TrackFile file, Task<TrackMetadata>? metadata,
        CancellationToken token, Task<CachedTrack>? previous)
    {
        if (previous is not null)
        {
            // Wait for the superseded writer to finish cleaning its file before replacing its result.
            try { return await previous.ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        await _decodeSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            TrackMetadata? tags = null;
            try
            {
                if (metadata is not null) tags = await metadata.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            var track = await preparation.ImportAsync(file, tags, token).ConfigureAwait(false);
            _decodedFiles[track] = file;
            return track;
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

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
        if (totalBytes > PcmBudgetBytes || coverBytes > ArtworkBudgetBytes)
        {
            var evicted = new List<CachedTrack>();
            foreach (var file in _pcmAccess.OrderBy(entry => entry.Value).Select(entry => entry.Key).ToArray())
            {
                if (totalBytes <= PcmBudgetBytes && coverBytes <= ArtworkBudgetBytes) break;
                if (file == selected || file == armed || upcoming.Contains(file) || GetPrepared(file) is not { } track ||
                    ReferenceEquals(track, current) || ReferenceEquals(track, incoming)) continue;

                _prepared.Remove(file);
                _pcmAccess.Remove(file);
                _decodedFiles.TryRemove(track, out _);
                totalBytes -= track.PcmSizeBytes;
                coverBytes -= track.Artwork.Length;
                evicted.Add(track);
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

    private static Task DeleteAsync(Task previous, List<CachedTrack> tracks) => Task.Run(async () =>
    {
        await previous.ConfigureAwait(false);
        foreach (var track in tracks) track.Delete();
    });

    /// <summary>Waits for all imports to stop before deleting their PCM caches.</summary>
    public async Task CloseAsync()
    {
        var tasks = _prepared.Values.Select(entry => entry.Work).ToArray();
        try
        {
            await Task.WhenAll(tasks.Cast<Task>().Concat(_metadata.Values).Concat(_queueMetadata.Values).Append(_cacheCleanup));
        }
        catch (Exception)
        {
            // Failed imports clean up their own temporary files.
        }
        foreach (var task in tasks)
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result.Delete();
            }
        }
        _decodeSlots.Dispose();
        _metadataSlots.Dispose();
        _thumbnailSlots.Dispose();
    }
}
