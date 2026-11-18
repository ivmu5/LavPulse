using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Holds the window's file selection and deduplicates background preparation requests.</summary>
internal sealed class PlayerLibrary(TrackPreparation preparation, CancellationToken lifetime)
{
    private const long PcmBudgetBytes = 256L * 1024 * 1024;
    private const long ArtworkBudgetBytes = 32L * 1024 * 1024;
    private const long ThumbnailBudgetBytes = 24L * 1024 * 1024;
    private readonly Dictionary<string, TrackFile> _paths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<TrackFile> _files = [];
    private readonly Dictionary<CachedTrack, TrackFile> _decodedFiles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TrackFile, long> _pcmAccess = new();
    private readonly Dictionary<TrackFile, long> _tagAccess = new();
    private readonly Dictionary<TrackFile, long> _thumbnailAccess = new();
    private long _access;
    private Task _cacheCleanup = Task.CompletedTask;
    private readonly Dictionary<TrackFile, Task<CachedTrack>> _prepared = new();
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
    public Task<CachedTrack> PrepareAsync(TrackFile file)
    {
        _pcmAccess[file] = ++_access;
        if (!_prepared.TryGetValue(file, out var task) || task.IsFaulted || task.IsCanceled)
        {
            task = DecodeAsync(file, _metadata.GetValueOrDefault(file));
            _prepared[file] = task;
        }
        return task;
    }

    public CachedTrack? GetPrepared(TrackFile file, bool forPlayback = false)
    {
        if (!_prepared.TryGetValue(file, out var task) || !task.IsCompletedSuccessfully) return null;
        if (forPlayback) _pcmAccess[file] = ++_access;
        return task.Result;
    }

    public TrackFile? Find(CachedTrack track)
    {
        if (!_decodedFiles.TryGetValue(track, out var file)) return null;
        _pcmAccess[file] = ++_access;
        return file;
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
        _tagAccess[file] = ++_access;
        if (!_metadata.TryGetValue(file, out var task) || task.IsFaulted || task.IsCanceled)
        {
            _metadata[file] = task = _prepared.TryGetValue(file, out var pending) && !pending.IsFaulted && !pending.IsCanceled
                ? ReadPreparedMetadataAsync(pending)
                : ReadTagsAsync(file);
        }
        return task;
    }

    private static async Task<TrackMetadata> ReadPreparedMetadataAsync(Task<CachedTrack> pending)
    {
        var track = await pending.ConfigureAwait(false);
        return new(track.Title, track.Artist, track.Artwork);
    }

    public TrackMetadata? GetMetadata(TrackFile file)
    {
        if (!_queueMetadata.TryGetValue(file, out var task) || !task.IsCompletedSuccessfully) return null;
        _thumbnailAccess[file] = ++_access;
        return task.Result;
    }

    public bool IsAnalyzing(TrackFile file) =>
        (_queueMetadata.TryGetValue(file, out var tags) && !tags.IsCompleted) ||
        (_prepared.TryGetValue(file, out var pcm) && !pcm.IsCompleted);

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
    private async Task<CachedTrack> DecodeAsync(TrackFile file, Task<TrackMetadata>? metadata)
    {
        await _decodeSlots.WaitAsync(lifetime);
        try
        {
            TrackMetadata? tags = null;
            try
            {
                if (metadata is not null) tags = await metadata;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }

            var track = await preparation.ImportAsync(file, tags, lifetime);
            _decodedFiles.Add(track, file);
            return track;
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    /// <summary>Evicts old decoded files and source artwork. Pending work and occupied decks stay pinned.</summary>
    public void Trim(CachedTrack? current, CachedTrack? incoming, TrackFile? selected, TrackFile? armed)
    {
        long totalBytes = 0;
        long coverBytes = 0;
        foreach (var task in _prepared.Values)
        {
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
                if (file == selected || file == armed || GetPrepared(file) is not { } track ||
                    ReferenceEquals(track, current) || ReferenceEquals(track, incoming)) continue;

                _prepared.Remove(file);
                _pcmAccess.Remove(file);
                _decodedFiles.Remove(track);
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
        var tasks = _prepared.Values.ToArray();
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
