using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

internal sealed record StoredTrack(string Id, string Name);
internal sealed record StoredQueue(StoredTrack[] Tracks, string? Current);
internal sealed record StoredAnalysis(long TotalFrames, int FramesPerPoint, WaveformPoint[] Points,
    TrackAudioInfo? AudioInfo, string? PcmFile)
{
    [JsonIgnore]
    public TrackWaveform Waveform => new(Array.AsReadOnly(Points), TotalFrames, FramesPerPoint);
}

[JsonSerializable(typeof(StoredQueue))]
[JsonSerializable(typeof(StoredAnalysis))]
internal sealed partial class TrackStoreJson : JsonSerializerContext;

/// <summary>Owns the imported MP3 copies, queue manifest and versioned analysis data.</summary>
internal sealed class LocalTrackStore(string root)
{
    internal const long PcmBudgetBytes = 256L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, byte> _analyzed = new();
    private readonly SemaphoreSlim _analysisWrites = new(1);
    private string MediaDirectory => Path.Combine(root, "media");
    private string AnalysisDirectory => Path.Combine(root, "analysis-v1");
    private string QueuePath => Path.Combine(root, "queue.json");
    public string PcmDirectory => Path.Combine(root, "pcm-v1");

    public async Task<TrackFile> CopyAsync(TrackFile file, CancellationToken token)
    {
        Directory.CreateDirectory(MediaDirectory);
        string temp = Path.Combine(MediaDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            string id;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await file.CopyToAsync(stream, token).ConfigureAwait(false);
                stream.Position = 0;
                id = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            }

            token.ThrowIfCancellationRequested();
            string path = MediaPath(id);
            if (!File.Exists(path))
            {
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            return TrackFile.FromPath(path) with { Name = file.Name };
        }
        finally
        {
            CachedTrack.DeleteCacheFile(temp);
        }
    }

    public async Task<(TrackFile[] Tracks, TrackFile? Current)> ReadQueueAsync(CancellationToken token)
    {
        var manifest = await ReadAsync(QueuePath, TrackStoreJson.Default.StoredQueue, token).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TrackFile>();
        TrackFile? current = null;
        foreach (var entry in manifest?.Tracks ?? [])
        {
            if (entry is null || !ValidId(entry.Id) || !TrackFile.IsMp3(entry.Name)) continue;
            string path = MediaPath(entry.Id);
            if (!File.Exists(path) || !seen.Add(entry.Id)) continue;
            var file = TrackFile.FromPath(path) with { Name = entry.Name };
            result.Add(file);
            if (entry.Id == manifest!.Current) current = file;
            if (File.Exists(AnalysisPath(file))) _analyzed[path] = 0;
        }
        string? activePcm = null;
        if ((current ?? result.FirstOrDefault()) is { } active &&
            await ReadAnalysisAsync(active, token).ConfigureAwait(false) is { } analysis)
        {
            activePcm = FindPcm(analysis);
        }
        PrunePcm(activePcm, token);
        return (result.ToArray(), current);
    }

    public Task SaveQueueAsync(StoredQueue queue) => WriteAsync(QueuePath, queue,
        TrackStoreJson.Default.StoredQueue, CancellationToken.None);

    public static StoredQueue Snapshot(IReadOnlyList<TrackFile> tracks, TrackFile? current) =>
        new(tracks.Select(file => new StoredTrack(Id(file), file.Name)).ToArray(), current is null ? null : Id(current));

    public bool HasAnalysis(TrackFile file) =>
        file.LocalPath is { } path && _analyzed.ContainsKey(path);

    public async Task<StoredAnalysis?> ReadAnalysisAsync(TrackFile file, CancellationToken token)
    {
        var analysis = await ReadAsync(AnalysisPath(file), TrackStoreJson.Default.StoredAnalysis, token).ConfigureAwait(false);
        if (analysis is { TotalFrames: > 0, FramesPerPoint: > 0, Points.Length: > 0 and <= 8192 } &&
            analysis.TotalFrames <= long.MaxValue / PlaybackFormat.Pcm16FrameBytes &&
            (analysis.TotalFrames - 1) / analysis.FramesPerPoint + 1 == analysis.Points.LongLength)
        {
            _analyzed[file.LocalPath!] = 0;
            return analysis;
        }

        _analyzed.TryRemove(file.LocalPath!, out _);
        return null;
    }

    public string? FindPcm(StoredAnalysis analysis)
    {
        if (analysis.PcmFile is not { } name || name != Path.GetFileName(name) ||
            !name.StartsWith("lavpulse-", StringComparison.Ordinal) || !name.EndsWith(".pcm", StringComparison.Ordinal))
            return null;

        string path = Path.Combine(PcmDirectory, name);
        var pcm = new FileInfo(path);
        return pcm.Exists && pcm.Length == analysis.TotalFrames * PlaybackFormat.Pcm16FrameBytes ? path : null;
    }

    public async Task SaveAnalysisAsync(TrackFile file, TrackWaveform waveform, TrackAudioInfo? info,
        string? pcm, CancellationToken token)
    {
        await _analysisWrites.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // A low-priority waveform scan must not replace a concurrently prepared PCM cache.
            if (pcm is null && HasAnalysis(file)) return;
            var analysis = new StoredAnalysis(waveform.TotalFrames, waveform.FramesPerPoint, waveform.Points.ToArray(),
                info, Path.GetFileName(pcm));
            await WriteAsync(AnalysisPath(file), analysis, TrackStoreJson.Default.StoredAnalysis, token).ConfigureAwait(false);
            _analyzed[file.LocalPath!] = 0;
        }
        finally { _analysisWrites.Release(); }
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

    private string MediaPath(string id) => Path.Combine(MediaDirectory, id + ".mp3");
    private string AnalysisPath(TrackFile file) => Path.Combine(AnalysisDirectory, Id(file) + ".json");
    private static string Id(TrackFile file) => Path.GetFileNameWithoutExtension(file.LocalPath!);
    private static bool ValidId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigitUpper);

    private static async Task<T?> ReadAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken token) where T : class
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync(stream, type, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteAsync<T>(string path, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, type, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { CachedTrack.DeleteCacheFile(temporary); }
    }
}
