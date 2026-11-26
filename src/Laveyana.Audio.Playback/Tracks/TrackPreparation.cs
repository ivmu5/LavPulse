using System.Runtime.InteropServices;

namespace Laveyana.Audio.Playback;

public sealed record TrackMetadata(string Title, string? Artist, ReadOnlyMemory<byte> Artwork)
{
    public string? Album { get; init; }
}

/// <summary>Prepares seekable PCM and a display waveform from supported audio.</summary>
public sealed class TrackPreparation(string cacheDirectory)
{
    private readonly Func<Stream, CancellationToken, IMp3SampleDecoder?>? _openPlatformDecoder;

    internal TrackPreparation(string cacheDirectory, Func<Stream, CancellationToken, IMp3SampleDecoder?> openPlatformDecoder)
        : this(cacheDirectory) => _openPlatformDecoder = openPlatformDecoder;

    /// <summary>Reads the display tags independently of PCM decoding and waveform analysis.</summary>
    public Task<TrackMetadata> ReadMetadataAsync(TrackFile file, CancellationToken token) => Task.Run(async () =>
    {
        TrackMetadata metadata;
        if (file.OpenReadAsync is { } open)
        {
            await using var source = await open().ConfigureAwait(false);
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
                await using var direct = file.OpenReadAsync is { } open ? await open().ConfigureAwait(false) : null;
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

    private static bool CanAnalyzeInPlace(Stream source)
    {
        if (!source.CanSeek) return false;
        try
        {
            _ = source.Length;
            source.Position = 0;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException)
        {
            return false;
        }
    }

    private CachedTrack Decode(Stream source, TrackFile file, string pcmPath, TrackMetadata? metadata,
        (TrackWaveform Waveform, TrackAudioInfo? Info)? analysis, CancellationToken token, bool parallelKey)
    {
        var decoded = DecodeWaveform(source, Path.GetExtension(file.Name), pcmPath, analysis?.Waveform, token, parallelKey: parallelKey);
        var waveform = decoded.Waveform;

        if (metadata is null)
        {
            source.Position = 0;
            metadata = ReadTags(file, source);
        }

        var audioInfo = TrackFile.IsMp3(file.Name)
            ? analysis?.Info ?? Mp3AudioInfoReader.Read(source, token) : decoded.Info ?? analysis?.Info;
        token.ThrowIfCancellationRequested();
        return new CachedTrack(file.Name, pcmPath, waveform, metadata, audioInfo);
    }

    private (TrackWaveform Waveform, TrackAudioInfo? Info) DecodeWaveform(Stream source,
        string extension, string? pcmPath, TrackWaveform? waveform, CancellationToken token, TrackRhythmTrace? trace = null, bool parallelKey = false)
    {
        extension = extension.ToLowerInvariant();
        bool mp3 = extension == ".mp3";
        bool vorbis = extension is ".ogg" or ".oga";
        long encodedBytes = source.Length;

        (TrackWaveform, TrackAudioInfo?) Record(IMp3SampleDecoder native, string backend)
        {
            int rate = native.SampleRate, channels = native.Channels;
            string codec = native.Codec ?? extension.TrimStart('.').ToUpperInvariant();
            int bits = native is WaveSampleDecoder wave ? wave.BitsPerSample : 0;
            using var decoder = new Mp3PcmSource(native);
            var result = AnalyzeOrRecord(decoder, pcmPath, waveform, token, trace, parallelKey);
            if (trace is not null) trace.Decoder = backend;
            if (mp3) return (result, null);
            int bitrate = bits > 0 ? (int)((long)rate * channels * bits / 1000)
                : (int)Math.Clamp(Math.Round(encodedBytes * 8d * PlaybackFormat.SampleRate /
                    Math.Max(1, result.TotalFrames) / 1000), 0, int.MaxValue);
            return (result, new TrackAudioInfo(codec, rate, bitrate, vorbis || extension == ".flac",
                channels == 1 ? TrackChannelMode.Mono : TrackChannelMode.Stereo));
        }

        if (extension == ".wav")
            return Record(new WaveSampleDecoder(source), "RIFF/WAVE");

        Exception? platformError = null;
        if (_openPlatformDecoder is not null)
        {
            try
            {
                if (_openPlatformDecoder(source, token) is { } platformDecoder)
                    return Record(platformDecoder, "platform");
            }
            catch (InvalidDataException ex)
            {
                token.ThrowIfCancellationRequested();
                platformError = ex;
                System.Diagnostics.Debug.WriteLine(ex);
                if (pcmPath is not null) File.Delete(pcmPath);
            }
            source.Position = 0;
        }

        if (mp3)
        {
            using var managedDecoder = new Mp3PcmSource(source);
            var result = AnalyzeOrRecord(managedDecoder, pcmPath, waveform, token, trace, parallelKey);
            if (trace is not null) trace.Decoder = "NLayer";
            return (result, null);
        }
        if (vorbis)
            return Record(new VorbisSampleDecoder(source), "NVorbis");
        throw new InvalidDataException("Системный декодер не поддерживает этот аудиофайл.", platformError);
    }

    private static TrackWaveform AnalyzeOrRecord(Mp3PcmSource decoder, string? pcmPath, TrackWaveform? waveform,
        CancellationToken token, TrackRhythmTrace? trace = null, bool parallelKey = false)
    {
        if (pcmPath is null) return TrackWaveform.Analyze(decoder, token, trace, parallelKey);

        long frames = 0;
        using (var pcm = new FileStream(pcmPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            var recording = new RecordingSource(decoder, pcm);
            if (waveform is null) return TrackWaveform.Analyze(recording, token, trace, parallelKey);

            var buffer = new float[4096 * PlaybackFormat.Channels];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = recording.Read(buffer);
                if (read == 0) break;
                frames += read;
            }
        }
        if (frames == waveform.TotalFrames) return waveform;

        // A different decoder can produce a different count of padded frames for the same MP3.
        using var recorded = new CachedPcmSource(pcmPath, new DeckCursor(frames, 0));
        return TrackWaveform.Analyze(recorded, token, trace, parallelKey);
    }

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

    /// <summary>Writes the same samples used by the waveform analysis to the PCM cache.</summary>
    private sealed class RecordingSource(Mp3PcmSource source, Stream output) : IPcmSource
    {
        private short[] _buffer = [];

        public int SampleRate => source.SampleRate;

        /// <summary>Records the decoded frames while passing them through to waveform analysis.</summary>
        public int Read(Span<float> destination)
        {
            int frames = source.Read(destination);
            int samples = frames * PcmMixer.ChannelCount;
            if (_buffer.Length < samples)
            {
                _buffer = new short[samples];
            }

            var pcm = _buffer.AsSpan(0, samples);
            Pcm16.Encode(destination[..samples], pcm);
            output.Write(MemoryMarshal.AsBytes(pcm));
            return frames;
        }
    }
}
