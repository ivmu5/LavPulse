using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Holds the window's file selection and deduplicates background preparation requests.</summary>
internal sealed class PlayerLibrary(TrackPreparation preparation, CancellationToken lifetime)
{
    private readonly List<TrackFile> _files = [];
    private readonly Dictionary<TrackFile, Task<CachedTrack>> _prepared = new();
    private readonly SemaphoreSlim _decodeSlots = new(2);


    /// <summary>Adds playable files while retaining stable identities for the queue and cached tracks.</summary>
    public IReadOnlyList<TrackFile> Add(IEnumerable<TrackFile> files)
    {
        var added = new List<TrackFile>();
        foreach (var file in files)
        {
            if (!TrackFile.IsMp3(file.Name) || _files.Contains(file) || file.LocalPath is { } path &&
                _files.Any(item => string.Equals(item.LocalPath, path, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                continue;
            }
            _files.Add(file);
            added.Add(file);
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
            await Task.WhenAll(tasks);
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
    }
}
