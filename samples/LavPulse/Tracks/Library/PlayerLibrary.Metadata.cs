using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

internal sealed partial class PlayerLibrary
{
    public bool HasQueueMetadataRequest(TrackFile file)
    {
        lock (_cacheGate) return _queueMetadata.ContainsKey(file);
    }

    public Task<TrackMetadata> ReadQueueMetadataAsync(TrackFile file)
    {
        lock (_cacheGate)
        {
            ThrowIfClosing();
            _thumbnailAccess[file] = ++_access;
            if (!_queueMetadata.TryGetValue(file, out var task) || task.IsFaulted || task.IsCanceled)
                _queueMetadata[file] = task = BuildQueueMetadataAsync(ReadMetadataAsync(file));
            return task;
        }
    }

    private async Task<TrackMetadata> BuildQueueMetadataAsync(Task<TrackMetadata> metadata)
    {
        var info = await metadata.ConfigureAwait(false);
        return await CreateThumbnailAsync(info, lifetime).ConfigureAwait(false);
    }

    private async Task<TrackMetadata> CreateThumbnailAsync(TrackMetadata info, CancellationToken token)
    {
        if (info.Artwork.IsEmpty) return info;

        await _thumbnailSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var thumbnail = await ArtworkThumbnail.CreateAsync(info.Artwork, token).ConfigureAwait(false);
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
            ThrowIfClosing();
            _tagAccess[file] = ++_access;
            if (!_metadata.TryGetValue(file, out var task) || task.IsFaulted || task.IsCanceled)
            {
                // Queue rows need tags while the deck is still decoding the audio.
                if (_prepared.TryGetValue(file, out var pending) && pending.Work.IsCompletedSuccessfully)
                {
                    var track = pending.Work.Result;
                    task = Task.FromResult(new TrackMetadata(track.Title, track.Artist, track.Artwork) { Album = track.Album });
                }
                else task = ReadTagsAsync(file);
                _metadata[file] = task;
            }
            return task;
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

    public Task<TrackMetadata> ReadLibraryMetadataAsync(TrackFile file, CancellationToken token) =>
        TrackLibraryWork(() => ReadLibraryThumbnailAsync(file, token));

    private async Task<TrackMetadata> ReadLibraryThumbnailAsync(TrackFile file, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        return await CreateThumbnailAsync(await ReadTagsAsync(file, linked.Token).ConfigureAwait(false), linked.Token).ConfigureAwait(false);
    }

    private Task<TrackMetadata> ReadTagsAsync(TrackFile file) => ReadTagsAsync(file, lifetime);

    private async Task<TrackMetadata> ReadTagsAsync(TrackFile file, CancellationToken token)
    {
        await _metadataSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await preparation.ReadMetadataAsync(file, token).ConfigureAwait(false);
        }
        finally
        {
            _metadataSlots.Release();
        }
    }
}
