namespace Laveyana.Audio.Playback;

/// <summary>Prepares seekable PCM and a display waveform from supported audio.</summary>
public sealed partial class TrackPreparation(string cacheDirectory)
{
    private readonly Func<Stream, CancellationToken, IMp3SampleDecoder?>? _openPlatformDecoder;

    internal TrackPreparation(string cacheDirectory, Func<Stream, CancellationToken, IMp3SampleDecoder?> openPlatformDecoder)
        : this(cacheDirectory) => _openPlatformDecoder = openPlatformDecoder;

    /// <summary>Reads the display tags independently of PCM decoding and waveform analysis.</summary>
    public Task<TrackMetadata> ReadMetadataAsync(TrackFile file, CancellationToken token) => Task.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(file);
        token.ThrowIfCancellationRequested();
        TrackMetadata metadata;
        if (file.OpenCopySource is { } open)
        {
            await using var source = await open().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            metadata = ReadTags(file, source);
        }
        else
        {
            Directory.CreateDirectory(cacheDirectory);
            string path = Path.Combine(cacheDirectory, $"lavpulse-tags-{Guid.NewGuid():N}{TrackFile.StorageExtension(file.Name)}");
            await using var source = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await file.CopyToAsync(source, token).ConfigureAwait(false);
            source.Position = 0;
            metadata = ReadTags(file, source);
        }
        token.ThrowIfCancellationRequested();
        return metadata;
    }, token);

    /// <summary>Imports one track and removes temporary files if decoding fails or is cancelled.</summary>
    public Task<CachedTrack> ImportAsync(TrackFile file, CancellationToken token) =>
        ImportAsync(file, null, token);

    /// <summary>Reuses tags already fetched for the queue and decodes directly from seekable MP3 streams.</summary>
    public Task<CachedTrack> ImportAsync(TrackFile file, TrackMetadata? metadata, CancellationToken token) =>
        ImportAsync(file, metadata, null, token);

    internal async Task<CachedTrack> ImportAsync(TrackFile file, TrackMetadata? metadata,
        (TrackWaveform Waveform, TrackAudioInfo? Info)? analysis, CancellationToken token, bool parallelKey = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!TrackFile.IsSupportedAudio(file.Name))
        {
            throw new InvalidDataException("Формат файла не поддерживается на этой платформе.");
        }

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheDirectory);
        string prefix = Path.Combine(cacheDirectory, $"lavpulse-{Guid.NewGuid():N}");
        string pcmPath = prefix + ".pcm";
        try
        {
            var track = await Task.Run(async () =>
            {
                await using var direct = file.OpenCopySource is { } open ? await open().ConfigureAwait(false) : null;
                token.ThrowIfCancellationRequested();
                if (direct is not null && CanAnalyzeInPlace(direct) &&
                    (direct is FileStream || TrackFile.IsMp3(file.Name) ||
                     Path.GetExtension(file.Name).ToLowerInvariant() is ".wav" or ".ogg" or ".oga"))
                {
                    return Decode(direct, file, pcmPath, metadata, analysis, token, parallelKey);
                }

                await using var copy = new FileStream(prefix + TrackFile.StorageExtension(file.Name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                if (direct is not null) await direct.CopyToAsync(copy, token).ConfigureAwait(false);
                else await file.CopyToAsync(copy, token).ConfigureAwait(false);
                copy.Position = 0;
                return Decode(copy, file, pcmPath, metadata, analysis, token, parallelKey);
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return track;
        }
        catch
        {
            CachedTrack.DeleteCacheFile(pcmPath);
            throw;
        }
    }

    /// <summary>Scans a locally imported track without producing a PCM file.</summary>
    internal Task<(TrackWaveform Waveform, TrackAudioInfo? Info)> AnalyzeAsync(string path, CancellationToken token, bool parallelKey = false) =>
        Task.Run(() =>
        {
            using var source = File.OpenRead(path);
            var decoded = DecodeWaveform(source, Path.GetExtension(path), null, null, token, parallelKey: parallelKey);
            return (decoded.Waveform, TrackFile.IsMp3(path) ? Mp3AudioInfoReader.Read(source, token) : decoded.Info);
        }, token);

    /// <summary>Reanalyzes an explicitly reported MP3 with full diagnostic trace,
    /// without creating PCM files, changing cached analysis or altering playback.</summary>
    internal Task<TrackDiagnosticSnapshot> AnalyzeDiagnosticAsync(string path, CancellationToken token) =>
        Task.Run(() =>
        {
            using var source = File.OpenRead(path);
            var trace = new TrackRhythmTrace();
            var decoded = DecodeWaveform(source, Path.GetExtension(path), null, null, token, trace);
            return new TrackDiagnosticSnapshot(decoded.Waveform,
                TrackFile.IsMp3(path) ? Mp3AudioInfoReader.Read(source, token) : decoded.Info, trace);
        }, token);

    private static TrackMetadata ReadTags(TrackFile file, Stream source)
    {
        string fallback = Path.GetFileNameWithoutExtension(file.Name);
        if (!TrackFile.IsMp3(file.Name))
        {
            if (source.CanSeek && (Path.GetExtension(file.Name).ToLowerInvariant() is ".ogg" or ".oga"))
            {
                try { source.Position = 0; return VorbisSampleDecoder.ReadTags(source, fallback); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
                { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
            return new TrackMetadata(fallback, null, ReadOnlyMemory<byte>.Empty);
        }
        var tags = Mp3Tags.Read(source);
        return new TrackMetadata(tags.Title ?? Path.GetFileNameWithoutExtension(file.Name), tags.Artist, tags.Artwork ?? [])
        {
            Album = tags.Album
        };
    }
}
