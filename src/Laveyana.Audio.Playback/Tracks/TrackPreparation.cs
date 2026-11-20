using System.Runtime.InteropServices;

namespace Laveyana.Audio.Playback;

public sealed record TrackMetadata(string Title, string? Artist, ReadOnlyMemory<byte> Artwork);

/// <summary>Prepares seekable PCM and a display waveform from an MP3 stream.</summary>
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
            string path = Path.Combine(cacheDirectory, $"lavpulse-tags-{Guid.NewGuid():N}.mp3");
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
    public async Task<CachedTrack> ImportAsync(TrackFile file, TrackMetadata? metadata, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!TrackFile.IsMp3(file.Name))
        {
            throw new InvalidDataException("Выбери файл с расширением .mp3.");
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
                if (direct is not null && CanAnalyzeInPlace(direct))
                {
                    return Decode(direct, file, pcmPath, metadata, token);
                }

                await using var copy = new FileStream(prefix + ".mp3", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                if (direct is not null) await direct.CopyToAsync(copy, token).ConfigureAwait(false);
                else await file.CopyToAsync(copy, token).ConfigureAwait(false);
                copy.Position = 0;
                return Decode(copy, file, pcmPath, metadata, token);
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
        CancellationToken token)
    {
        var waveform = DecodePcm(source, pcmPath, token);

        if (metadata is null)
        {
            source.Position = 0;
            metadata = ReadTags(file, source);
        }

        var audioInfo = Mp3AudioInfoReader.Read(source, token);
        token.ThrowIfCancellationRequested();
        return new CachedTrack(file.Name, pcmPath, waveform, metadata, audioInfo);
    }

    private TrackWaveform DecodePcm(Stream source, string pcmPath, CancellationToken token)
    {
        if (_openPlatformDecoder is not null)
        {
            try
            {
                if (_openPlatformDecoder(source, token) is { } platformDecoder)
                {
                    using var decoder = new Mp3PcmSource(platformDecoder);
                    return WritePcm(decoder, pcmPath, token);
                }
            }
            catch (InvalidDataException ex)
            {
                // Retry from the beginning with NLayer after a rejected stream or a stalled decoder.
                token.ThrowIfCancellationRequested();
                System.Diagnostics.Debug.WriteLine(ex);
                File.Delete(pcmPath);
            }
            source.Position = 0;
        }

        using var managedDecoder = new Mp3PcmSource(source);
        return WritePcm(managedDecoder, pcmPath, token);
    }

    private static TrackWaveform WritePcm(Mp3PcmSource decoder, string pcmPath, CancellationToken token)
    {
        using var pcm = new FileStream(pcmPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
        return TrackWaveform.Analyze(new RecordingSource(decoder, pcm), token);
    }

    private static TrackMetadata ReadTags(TrackFile file, Stream source)
    {
        var tags = Mp3Tags.Read(source);
        return new TrackMetadata(tags.Title ?? Path.GetFileNameWithoutExtension(file.Name), tags.Artist, tags.Artwork ?? []);
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
