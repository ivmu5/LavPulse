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
            analysis.Rhythm is { } rhythm && TrackRhythmAnalyzer.IsValid(rhythm, analysis.TotalFrames))
        {
            _analyzed[file.LocalPath!] = 0;
            return analysis;
        }

        _analyzed.TryRemove(file.LocalPath!, out _);
        return null;
    }

    public string? FindPcm(StoredAnalysis analysis)
    {
        if (PcmPath(analysis.PcmFile) is not { } path) return null;
        var pcm = new FileInfo(path);
        return pcm.Exists && pcm.Length == analysis.TotalFrames * PlaybackFormat.Pcm16FrameBytes ? path : null;
    }

    private string? PcmPath(string? name) => name is not null && name == Path.GetFileName(name) &&
        name.StartsWith("lavpulse-", StringComparison.Ordinal) && name.EndsWith(".pcm", StringComparison.Ordinal)
        ? Path.Combine(PcmDirectory, name) : null;

    /// <summary>Measures the saved MP3, analysis manifest and any associated PCM on disk.</summary>
    public async ValueTask<long?> ReadTrackStorageSizeAsync(TrackFile file, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (file.LocalPath is not { } media || FileSize(media) is not { } total) return null;
        string analysisPath = AnalysisPath(file);
        if (!File.Exists(analysisPath)) return total;
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
        var info = new FileInfo(path);
        return info.Exists ? info.Length : null;
    }

    public void EvictPcm(TrackFile file, CachedTrack track)
    {
        track.Delete();
        TrackStorageChanged?.Invoke(file.LocalPath!);
    }

    public async Task SaveAnalysisAsync(TrackFile file, TrackWaveform waveform, TrackAudioInfo? info,
        string? pcm, CancellationToken token, bool replaceAnalysis = false)
    {
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
                Rhythm = waveform.Rhythm ?? TrackRhythmAnalyzer.Analyze(waveform)
            };
            await WriteAsync(AnalysisPath(file), analysis, TrackStoreJson.Default.StoredAnalysis, token).ConfigureAwait(false);
            _analyzed[file.LocalPath!] = 0;
        }
        finally { _analysisWrites.Release(); }
        TrackStorageChanged?.Invoke(file.LocalPath!);
    }

    private void PrunePcm(string? activePcm, CancellationToken token)
    {
        if (!Directory.Exists(PcmDirectory)) return;
        long bytes = 0;
        foreach (var pcm in new DirectoryInfo(PcmDirectory).EnumerateFiles("lavpulse-*.pcm")
            .OrderByDescending(file => file.FullName == activePcm).ThenByDescending(file => file.LastWriteTimeUtc))
        {
            token.ThrowIfCancellationRequested();
            if (pcm.FullName != activePcm && pcm.Length > PcmBudgetBytes - bytes) CachedTrack.DeleteCacheFile(pcm.FullName);
            else bytes += pcm.Length;
        }
    }
}
