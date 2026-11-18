using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Holds the window's file selection and deduplicates background preparation requests.</summary>
internal sealed class PlayerLibrary(TrackPreparation preparation, CancellationToken lifetime)
{
    private readonly List<TrackFile> _files = [];
    private readonly Dictionary<TrackFile, Task<CachedTrack>> _prepared = new();
    private readonly Dictionary<TrackFile, Task<TrackMetadata>> _metadata = new();
    private readonly SemaphoreSlim _decodeSlots = new(2);
    private readonly SemaphoreSlim _metadataSlots = new(3);

    /// <summary>Adds playable files while retaining stable identities for the queue and cached tracks.</summary>
    public IReadOnlyList<TrackFile> Add(IEnumerable<TrackFile> files, IReadOnlyList<TrackFile> queue)
    {
        var added = new List<TrackFile>();
        foreach (var file in files)
        {
            if (!TrackFile.IsMp3(file.Name)) continue;
            var existing = _files.FirstOrDefault(item => item == file || file.LocalPath is { } path &&
                string.Equals(item.LocalPath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            var canonical = existing ?? file;
            if (queue.Contains(canonical) || added.Contains(canonical)) continue;
            if (existing is null) _files.Add(canonical);
            added.Add(canonical);
        }
        return added;
    }

    /// <summary>Prepares a track once. A failed import can be retried through the library.</summary>
    public Task<CachedTrack> PrepareAsync(TrackFile file)
    {
        if (!_prepared.TryGetValue(file, out var task) || task.IsFaulted || task.IsCanceled)
        {
            task = DecodeAsync(file);
            _prepared[file] = task;
        }
        return task;
    }

    public CachedTrack? GetPrepared(TrackFile file) =>
        _prepared.TryGetValue(file, out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    public TrackFile? Find(CachedTrack track) => _files.FirstOrDefault(file => ReferenceEquals(GetPrepared(file), track));

    /// <summary>Reads queue tags in parallel without making playback wait for the rest of a folder.</summary>
    public Task<TrackMetadata> ReadMetadataAsync(TrackFile file)
    {
        if (!_metadata.TryGetValue(file, out var task))
        {
            _metadata[file] = task = ReadTagsAsync(file);
        }
        return task;
    }

    public TrackMetadata? GetMetadata(TrackFile file) =>
        _metadata.TryGetValue(file, out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    public bool IsAnalyzing(TrackFile file) =>
        (_metadata.TryGetValue(file, out var tags) && !tags.IsCompleted) ||
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
    private async Task<CachedTrack> DecodeAsync(TrackFile file)
    {
        await _decodeSlots.WaitAsync(lifetime);
        try
        {
            return await preparation.ImportAsync(file, lifetime);
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    /// <summary>Waits for all imports to stop before deleting their PCM caches.</summary>
    public async Task CloseAsync()
    {
        var tasks = _prepared.Values.ToArray();
        try
        {
            await Task.WhenAll(tasks.Cast<Task>().Concat(_metadata.Values));
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
    }
}
