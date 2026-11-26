using System.Text.Json;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

internal sealed partial class LocalTrackStore
{
    public bool HasAnalysis(TrackFile file) =>
        file.LocalPath is { } path && _analyzed.ContainsKey(path);

    public async Task<StoredAnalysis?> ReadAnalysisAsync(TrackFile file, CancellationToken token)
    {
        var analysis = await ReadAsync(AnalysisPath(file), TrackStoreJson.Default.StoredAnalysis, token).ConfigureAwait(false);
        if (analysis is { TotalFrames: > 0, FramesPerPoint: > 0, Points.Length: > 0 and <= 8192 } &&
            analysis.TotalFrames <= long.MaxValue / PlaybackFormat.Pcm16FrameBytes &&
            (analysis.TotalFrames - 1) / analysis.FramesPerPoint + 1 == analysis.Points.LongLength &&
            analysis.Points.All(p => float.IsFinite(p.Peak) && p.Peak >= 0 &&
                float.IsFinite(p.Low) && p.Low >= 0 && float.IsFinite(p.Mid) && p.Mid >= 0 &&
                float.IsFinite(p.High) && p.High >= 0) &&
            analysis.Rhythm is { } rhythm && TrackRhythmAnalyzer.IsValid(rhythm, analysis.TotalFrames) &&
            (analysis.MusicalKey is null || analysis.MusicalKey.IsValid))
        {
            // An invalid optional profile must not invalidate a usable beat grid or PCM.
            if (analysis.MusicProfile is { } profile && !profile.IsValid(analysis.TotalFrames))
                analysis = analysis with { MusicProfile = null };
            // Derive once on the asynchronous cache-read path, not every
            // time a caller requests the waveform. Preserve the original PCM.
            if (analysis.MusicProfile is not { HasCurrentDerived: true })
                analysis = analysis with { MusicProfile = TrackMusicProfileAnalyzer.Derive(
                    analysis.Waveform, analysis.MusicProfile, token) };
            _analyzed[file.LocalPath!] = 0;
            return analysis;
        }

        _analyzed.TryRemove(file.LocalPath!, out _);
        return null;
    }

    public string? FindPcm(StoredAnalysis analysis)
    {
        if (analysis.TotalFrames <= 0 ||
            analysis.TotalFrames > long.MaxValue / PlaybackFormat.Pcm16FrameBytes ||
            PcmPath(analysis.PcmFile) is not { } path) return null;
        var pcm = new FileInfo(path);
        return pcm.Exists && pcm.Length == analysis.TotalFrames * PlaybackFormat.Pcm16FrameBytes ? path : null;
    }

    private string? PcmPath(string? name) => name is not null && name == Path.GetFileName(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('\\') && !name.Contains('/') &&
        name.StartsWith("lavpulse-", StringComparison.Ordinal) && name.EndsWith(".pcm", StringComparison.Ordinal)
        ? Path.Combine(PcmDirectory, name) : null;

    private string RequireOwnedPcm(string pcm)
    {
        string full = Path.GetFullPath(pcm);
        string? owned = PcmPath(Path.GetFileName(full));
        if (owned is null || !LibraryStorageMigration.SamePath(full, owned))
            throw new ArgumentException("PCM path is outside the managed cache.", nameof(pcm));
        return owned;
    }

    /// <summary>Measures the saved MP3, analysis manifest and any associated PCM on disk.</summary>
    public async ValueTask<long?> ReadTrackStorageSizeAsync(TrackFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (file.LocalPath is not { } media || FileSize(media) is not { } total) return null;
        string analysisPath = AnalysisPath(file);
        try
        {
            // Read only the cache reference. The waveform array is skipped by the deserializer.
            await using var stream = new FileStream(analysisPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous);
            total += stream.Length;
            var reference = await JsonSerializer.DeserializeAsync(stream, TrackStoreJson.Default.StoredPcmReference, token)
                .ConfigureAwait(false);
            if (PcmPath(reference?.PcmFile) is { } pcm) total += FileSize(pcm) ?? 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return total;
    }

    private static long? FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void EvictPcm(TrackFile file, CachedTrack track) => EvictPcm(file, track.Path);

    internal void EvictPcm(TrackFile file, string pcm)
    {
        CachedTrack.DeleteCacheFile(RequireOwnedPcm(pcm));
        NotifyStorageChanged(file.LocalPath!);
    }

    // Called after a prepared entry takes responsibility for the persisted PCM.
    internal void ClaimRestoredPcm(TrackFile file)
    {
        if (file.LocalPath is { } path) _restoredPcm.TryRemove(path, out _);
    }

    // Called under the player's cache gate. A new preparation must await the
    // resulting cleanup task before reading the saved PCM reference.
    internal List<(TrackFile File, string Pcm)> TakeUnclaimedPcm(IReadOnlyCollection<TrackFile> retained)
    {
        var removed = new List<(TrackFile File, string Pcm)>();
        if (_restoredPcm.IsEmpty) return removed;
        var paths = new HashSet<string>(retained.Where(file => file.LocalPath is not null)
            .Select(file => file.LocalPath!), _restoredPcm.Comparer);
        foreach (var item in _restoredPcm)
            if (!paths.Contains(item.Key) && _restoredPcm.TryRemove(item.Key, out var cache))
                removed.Add(cache);
        return removed;
    }

    public async Task SaveAnalysisAsync(TrackFile file, TrackWaveform waveform, TrackAudioInfo? info,
        string? pcm, CancellationToken token, bool replaceAnalysis = false)
    {
        token.ThrowIfCancellationRequested();
        if (pcm is not null) pcm = RequireOwnedPcm(pcm);
        if (pcm is null && !replaceAnalysis && HasAnalysis(file)) return;
        var rhythm = waveform.Rhythm ?? TrackRhythmAnalyzer.Analyze(waveform);
        var profile = TrackMusicProfileAnalyzer.Derive(waveform with { Rhythm = rhythm }, waveform.MusicProfile, token);
        await _analysisWrites.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A background scan cannot replace a prepared cache. An explicit scan may refresh
            // the waveform while retaining compatible PCM that is still used for playback.
            if (pcm is null && HasAnalysis(file))
            {
                if (!replaceAnalysis) return;
                if (await ReadAnalysisAsync(file, token).ConfigureAwait(false) is { } previous &&
                    previous.TotalFrames == waveform.TotalFrames)
                    pcm = FindPcm(previous);
            }
            var analysis = new StoredAnalysis(waveform.TotalFrames, waveform.FramesPerPoint, waveform.Points.ToArray(),
                info, Path.GetFileName(pcm))
            {
                Rhythm = rhythm,
                MusicalKey = waveform.MusicalKey,
                MusicProfile = profile
            };
            await WriteAsync(AnalysisPath(file), analysis, TrackStoreJson.Default.StoredAnalysis, token).ConfigureAwait(false);
            _analyzed[file.LocalPath!] = 0;
        }
        finally { _analysisWrites.Release(); }
        NotifyStorageChanged(file.LocalPath!);
    }

    // Notification is advisory and occurs after committing the file.
    // A subscriber failure must not make the importer delete committed PCM.
    private void NotifyStorageChanged(string path)
    {
        if (TrackStorageChanged is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(path); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    private void PrunePcm(IReadOnlySet<string> retained, CancellationToken token)
    {
        if (!Directory.Exists(PcmDirectory)) return;
        foreach (var pcm in Directory.EnumerateFiles(PcmDirectory, "lavpulse-*.pcm"))
        {
            token.ThrowIfCancellationRequested();
            if (!retained.Contains(pcm)) CachedTrack.DeleteCacheFile(pcm);
        }
    }
}
