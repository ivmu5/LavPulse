using Laveyana.Audio.Playback;
using System.Collections.Concurrent;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Holds the window's file selection and deduplicates background preparation requests.</summary>
internal sealed partial class PlayerLibrary(TrackPreparation preparation, LocalTrackStore store, CancellationToken lifetime)
{
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
    private readonly Dictionary<TrackFile, int> _analyzing = new();
    private readonly HashSet<Task> _libraryWork = [];
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
                pending = new(DecodeAsync(file, metadata, token, pending?.Work, _cacheCleanup), token);
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

    public bool IsPreparing(TrackFile file)
    {
        lock (_cacheGate)
            return _analyzing.ContainsKey(file) ||
                (_prepared.TryGetValue(file, out var pending) && !pending.Token.IsCancellationRequested && !pending.Work.IsCompleted);
    }

    public bool HasAnalysis(TrackFile file) => store.HasAnalysis(file);

    public Task AnalyzeAsync(TrackFile file) => ScanAsync(file, replaceAnalysis: false, lifetime);

    public Task RescanAsync(TrackFile file, CancellationToken token) =>
        TrackLibraryWork(ScanAsync(file, replaceAnalysis: true, token));

    private T TrackLibraryWork<T>(T work) where T : Task
    {
        if (!work.IsCompleted)
        {
            lock (_cacheGate) _libraryWork.Add(work);
            _ = ForgetLibraryWorkAsync(work);
        }
        return work;
    }

    private async Task ForgetLibraryWorkAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception)
        {
            // The caller of the original task handles cancellations and errors.
        }
        finally { lock (_cacheGate) _libraryWork.Remove(work); }
    }

    private async Task ScanAsync(TrackFile file, bool replaceAnalysis, CancellationToken token)
    {
        await _decodeSlots.WaitAsync(token).ConfigureAwait(false);
        bool scanning = false;
        try
        {
            if (!replaceAnalysis && store.HasAnalysis(file)) return;
            lock (_cacheGate) _analyzing[file] = _analyzing.GetValueOrDefault(file) + 1;
            scanning = true;
            var analysis = await preparation.AnalyzeAsync(file.LocalPath!, token).ConfigureAwait(false);
            await store.SaveAnalysisAsync(file, analysis.Waveform, analysis.Info, null, token, replaceAnalysis)
                .ConfigureAwait(false);
        }
        finally
        {
            if (scanning)
            {
                lock (_cacheGate)
                {
                    if (_analyzing[file] == 1) _analyzing.Remove(file);
                    else _analyzing[file]--;
                }
            }
            _decodeSlots.Release();
        }
    }

    /// <summary>Limits simultaneous MP3 analysis on mobile while letting playback continue.</summary>
    private async Task<CachedTrack> DecodeAsync(TrackFile file, Task<TrackMetadata>? metadata,
        CancellationToken token, Task<CachedTrack>? previous, Task cacheCleanup)
    {
        if (previous is not null)
        {
            // Wait for the superseded writer to finish cleaning its file before replacing its result.
            try { return await previous.ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        // A prior eviction may still be deleting the PCM named by this track's saved analysis.
        await cacheCleanup.WaitAsync(token).ConfigureAwait(false);
        TrackMetadata? tags = null;
        try
        {
            if (metadata is not null) tags = await metadata.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        var analysis = await store.ReadAnalysisAsync(file, token).ConfigureAwait(false);
        if (analysis is not null && store.FindPcm(analysis) is { } pcm)
        {
            tags ??= await preparation.ReadMetadataAsync(file, token).ConfigureAwait(false);
            var cached = new CachedTrack(file.Name, pcm, analysis.Waveform, tags, analysis.AudioInfo);
            _decodedFiles[cached] = file;
            return cached;
        }

        // Only a cache miss competes with background work for a decoder.
        await _decodeSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var track = await preparation.ImportAsync(file, tags,
                analysis is null ? null : (analysis.Waveform, analysis.AudioInfo), token).ConfigureAwait(false);
            try
            {
                await store.SaveAnalysisAsync(file, track.Waveform, track.AudioInfo, track.Path, token).ConfigureAwait(false);
            }
            catch
            {
                track.Delete();
                throw;
            }
            _decodedFiles[track] = file;
            return track;
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    /// <summary>Waits for pending imports and leaves complete PCM caches available for the next launch.</summary>
    public async Task CloseAsync()
    {
        Task[] tasks;
        lock (_cacheGate)
        {
            tasks = _prepared.Values.Select(entry => (Task)entry.Work).Concat(_metadata.Values)
                .Concat(_queueMetadata.Values).Concat(_libraryWork).Append(_cacheCleanup).ToArray();
        }
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // Failed imports clean up their own temporary files.
        }
        _decodeSlots.Dispose();
        _metadataSlots.Dispose();
        _thumbnailSlots.Dispose();
    }
}
